﻿using Yuniql.Extensibility;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.IO;

namespace Yuniql.Core
{
    //TOD: Rename MigrationServiceTransactional
    /// <inheritdoc />
    public class MigrationServiceTransactional : MigrationServiceBase
    {
        private readonly IWorkspaceService _workspaceService;
        private readonly IDataService _dataService;
        private readonly IBulkImportService _bulkImportService;
        private readonly ITokenReplacementService _tokenReplacementService;
        private readonly IDirectoryService _directoryService;
        private readonly IFileService _fileService;
        private readonly ITraceService _traceService;
        private readonly IConfigurationService _configurationService;
        private readonly IMetadataService _metadataService;

        /// <inheritdoc /> 
        public MigrationServiceTransactional(
            IWorkspaceService workspaceService,
            IDataService dataService,
            IBulkImportService bulkImportService,
            IMetadataService metadataService,
            ITokenReplacementService tokenReplacementService,
            IDirectoryService directoryService,
            IFileService fileService,
            ITraceService traceService,
            IConfigurationService configurationService)
            : base(
                workspaceService,
                dataService,
                bulkImportService,
                metadataService,
                tokenReplacementService,
                directoryService,
                fileService,
                traceService,
                configurationService
            )
        {
            this._workspaceService = workspaceService;
            this._dataService = dataService;
            this._bulkImportService = bulkImportService;
            this._tokenReplacementService = tokenReplacementService;
            this._directoryService = directoryService;
            this._fileService = fileService;
            this._traceService = traceService;
            this._configurationService = configurationService;
            this._metadataService = metadataService;
        }

        /// <inheritdoc />
        public override void Run()
        {
            var configuration = _configurationService.GetConfiguration();
            if (!configuration.IsInitialized)
                Initialize();

            Run(
               workspace: configuration.Workspace,
               targetVersion: configuration.TargetVersion,
               isAutoCreateDatabase: configuration.IsAutoCreateDatabase,
               tokens: configuration.Tokens,
               isVerifyOnly: configuration.IsVerifyOnly,
               bulkSeparator: configuration.BulkSeparator,
               metaSchemaName: configuration.MetaSchemaName,
               metaTableName: configuration.MetaTableName,
               commandTimeout: configuration.CommandTimeout,
               bulkBatchSize: configuration.BulkBatchSize,
               appliedByTool: configuration.AppliedByTool,
               appliedByToolVersion: configuration.AppliedByToolVersion,
               environment: configuration.Environment,
               isContinueAfterFailure: configuration.IsContinueAfterFailure,
               transactionMode: configuration.TransactionMode,
               isRequiredClearedDraft: configuration.IsRequiredClearedDraft
            );
        }

        /// <inheritdoc />
        public override void Run(
            string workspace,
            string targetVersion = null,
            bool? isAutoCreateDatabase = false,
            List<KeyValuePair<string, string>> tokens = null,
            bool? isVerifyOnly = false,
            string bulkSeparator = null,
            string metaSchemaName = null,
            string metaTableName = null,
            int? commandTimeout = null,
            int? bulkBatchSize = null,
            string appliedByTool = null,
            string appliedByToolVersion = null,
            string environment = null,
            bool? isContinueAfterFailure = null,
            string transactionMode = null,
            bool isRequiredClearedDraft = false
         )
        {
            //print run configuration information            
            _traceService.Info($"Run configuration: {Environment.NewLine}{_configurationService.PrintAsJson()}");

            //check the workspace structure if required directories are present
            _workspaceService.Validate(workspace);

            //when uncomitted run is not supported, fail migration, throw exceptions and return error exit code
            if (isVerifyOnly.HasValue && isVerifyOnly == true && !_dataService.IsTransactionalDdlSupported)
            {
                throw new NotSupportedException("Yuniql.Verify is not supported in the target platform. " +
                    "The feature requires support for atomic DDL operations. " +
                    "An atomic DDL operations ensures creation of tables, views and other objects and data are rolledback in case of error. " +
                    "For more information see https://yuniql.io/docs/.");
            }

            //when no target version specified, we use the latest local version available
            if (string.IsNullOrEmpty(targetVersion))
            {
                targetVersion = _workspaceService.GetLatestVersion(workspace);
                _traceService.Info($"No explicit target version requested. We'll use latest available locally {targetVersion} on {workspace}.");
            }

            var connectionInfo = _dataService.GetConnectionInfo();
            var targetDatabaseName = connectionInfo.Database;
            var targetDatabaseServer = connectionInfo.DataSource;

            //we try to auto-create the database, we need this to be outside of the transaction scope
            //in an event of failure, users have to manually drop the auto-created database!
            //we only check if the db exists when --auto-create-db is true
            if (isAutoCreateDatabase.HasValue && isAutoCreateDatabase == true)
            {
                //we only check if the db exists when --auto-create-db is true
                var targetDatabaseExists = _metadataService.IsDatabaseExists();
                if (!targetDatabaseExists)
                {
                    _traceService.Info($"Target database does not exist. Creating database {targetDatabaseName} on {targetDatabaseServer}.");
                    _metadataService.CreateDatabase();
                    _traceService.Info($"Created database {targetDatabaseName} on {targetDatabaseServer}.");
                }
            }

            //check if database has been pre-configured to support migration and setup when its not
            var targetDatabaseConfigured = _metadataService.IsDatabaseConfigured(metaSchemaName, metaTableName);
            if (!targetDatabaseConfigured)
            {
                //create custom schema when user supplied and only if platform supports it
                if (_dataService.IsSchemaSupported && null != metaSchemaName && !_dataService.SchemaName.Equals(metaSchemaName))
                {
                    _traceService.Info($"Target schema does not exist. Creating schema {metaSchemaName} on {targetDatabaseName} on {targetDatabaseServer}.");
                    _metadataService.CreateSchema(metaSchemaName);
                    _traceService.Info($"Created schema {metaSchemaName} on {targetDatabaseName} on {targetDatabaseServer}.");
                }

                //create empty versions tracking table
                _traceService.Info($"Target database {targetDatabaseName} on {targetDatabaseServer} not yet configured for migration.");
                _metadataService.ConfigureDatabase(metaSchemaName, metaTableName);
                _traceService.Info($"Configured database migration support for {targetDatabaseName} on {targetDatabaseServer}.");
            }

            var targetDatabaseUpdated = _metadataService.UpdateDatabaseConfiguration(metaSchemaName, metaTableName);
            if (targetDatabaseUpdated)
                _traceService.Info($"The configuration of migration has been updated for {targetDatabaseName} on {targetDatabaseServer}.");
            else
                _traceService.Debug($"The configuration of migration is up to date for {targetDatabaseName} on {targetDatabaseServer}.");

            TransactionContext transactionContext = null;

            //check for presence of failed no-transactional versions from previous runs
            var allVersions = _metadataService.GetAllVersions(metaSchemaName, metaTableName);
            var failedVersion = allVersions.Where(x => x.Status == Status.Failed).FirstOrDefault();
            if (failedVersion != null)
            {
                //check if user had issue resolving option such as continue on failure
                if (isContinueAfterFailure == null)
                {
                    //program should exit with non zero exit code
                    var message = @$"Previous migration of ""{failedVersion.Version}"" version was not running in transaction and has failed when executing of script ""{failedVersion.FailedScriptPath}"" with following error: {failedVersion.FailedScriptError}. {MESSAGES.ManualResolvingAfterFailureMessage}";
                    _traceService.Error(message);
                    throw new InvalidOperationException(message);
                }

                _traceService.Info($@"The non-transactional failure resolving option ""{isContinueAfterFailure}"" was used. Version scripts already applied by previous migration run will be skipped.");
                transactionContext = new TransactionContext(failedVersion, isContinueAfterFailure.Value);
            }
            else
            {
                //check if the non-txn option is passed even if there was no previous failed runs
                if (isContinueAfterFailure != null)
                {
                    //program should exit with non zero exit code
                    _traceService.Info(@$"The transaction handling parameter --continue-after-failure received ""{isContinueAfterFailure}"" but no previous failed migrations recorded.");
                }
            }

            var appliedVersions = _metadataService.GetAllAppliedVersions(metaSchemaName, metaTableName)
                .Select(dv => dv.Version)
                .OrderBy(v => v)
                .ToList();

            //check if target database already runs the latest version and skips work if it already is
            var targeDatabaseLatest = IsTargetDatabaseLatest(targetVersion, metaSchemaName, metaTableName);
            if (!targeDatabaseLatest)
            {
                //enclose all executions in a single transaction, in the event of failure we roll back everything
                using (var connection = _dataService.CreateConnection())
                {
                    connection.Open();
                    using (var transaction = (!string.IsNullOrEmpty(transactionMode) && transactionMode.Equals(TRANSACTION_MODE.SESSION)) ? connection.BeginTransaction() : null)
                    {
                        try
                        {
                            //run all migrations present in all directories
                            if (null != transaction)
                                _traceService.Info("Transaction created for current session. This migration run will be executed in a shared connection and transaction context.");

                            RunAllInternal(connection, transaction, isRequiredClearedDraft);

                            //when true, the execution is an uncommitted transaction 
                            //and only for purpose of testing if all can go well when it run to the target environment
                            if (isVerifyOnly.HasValue && isVerifyOnly == true)
                                transaction?.Rollback();
                            else
                                transaction?.Commit();
                        }
                        catch (Exception)
                        {
                            transaction?.Rollback();
                            throw;
                        }
                    }
                }
            }
            else
            {
                //enclose all executions in a single transaction
                using (var connection = _dataService.CreateConnection())
                {
                    connection.Open();
                    using (var transaction = (!string.IsNullOrEmpty(transactionMode) && transactionMode.Equals(TRANSACTION_MODE.SESSION)) ? connection.BeginTransaction() : null)
                    {
                        try
                        {
                            //run all scripts present in the _pre, _draft and _post directories
                            if (null != transaction)
                                _traceService.Info("Transaction created for current session. This migration run will be executed in a shared connection and transaction context.");

                            RunDraftInternal(connection, transaction, isRequiredClearedDraft);

                            //when true, the execution is an uncommitted transaction 
                            //and only for purpose of testing if all can go well when it run to the target environment
                            if (isVerifyOnly.HasValue && isVerifyOnly == true)
                                transaction?.Rollback();
                            else
                                transaction?.Commit();
                        }
                        catch (Exception)
                        {
                            transaction?.Rollback();
                            throw;
                        }
                    }
                }
                _traceService.Info($"Target database runs the latest version already. Scripts in {RESERVED_DIRECTORY_NAME.PRE}, {RESERVED_DIRECTORY_NAME.DRAFT} and {RESERVED_DIRECTORY_NAME.POST} are executed.");
            }

            //local method
            void RunAllInternal(IDbConnection connection, IDbTransaction transaction, bool isRequiredClearedDraft)
            {
                //check if database has been pre-configured and execute init scripts
                if (!targetDatabaseConfigured)
                {
                    //runs all scripts in the _init folder
                    RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.INIT), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode);
                    _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.INIT)}");
                }

                //checks if target database already runs the latest version and skips work if it already is
                //runs all scripts in the _pre folder and subfolders
                RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.PRE), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode);
                _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.PRE)}");

                //runs all scripts int the vxx.xx folders and subfolders
                RunVersionScripts(connection, transaction, appliedVersions, workspace, targetVersion, transactionContext, tokens, bulkSeparator: bulkSeparator, metaSchemaName: metaSchemaName, metaTableName: metaTableName, commandTimeout: commandTimeout, bulkBatchSize: bulkBatchSize, appliedByTool: appliedByTool, appliedByToolVersion: appliedByToolVersion, environment: environment, transactionMode: transactionMode);

                //runs all scripts in the _draft folder and subfolders
                RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.DRAFT), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode, isRequiredClearedDraft: isRequiredClearedDraft);
                _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.DRAFT)}");

                //runs all scripts in the _post folder and subfolders
                RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.POST), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode);
                _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.POST)}");
            }

            //local method
            void RunDraftInternal(IDbConnection connection, IDbTransaction transaction, bool requiredClearedDraft)
            {
                //runs all scripts in the _pre folder and subfolders
                RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.PRE), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode);
                _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.PRE)}");

                //runs all scripts in the _draft folder and subfolders
                RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.DRAFT), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode, isRequiredClearedDraft: requiredClearedDraft);
                _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.DRAFT)}");

                //runs all scripts in the _post folder and subfolders
                RunNonVersionScripts(connection, transaction, Path.Combine(workspace, RESERVED_DIRECTORY_NAME.POST), tokens, bulkSeparator: bulkSeparator, commandTimeout: commandTimeout, environment: environment, transactionMode: transactionMode);
                _traceService.Info($"Executed script files on {Path.Combine(workspace, RESERVED_DIRECTORY_NAME.POST)}");
            }
        }

        /// <inheritdoc />
        public override void RunVersionScripts(
            IDbConnection connection,
            IDbTransaction transaction,
            List<string> appliedVersions,
            string workspace,
            string targetVersion,
            TransactionContext transactionContext,
            List<KeyValuePair<string, string>> tokens = null,
            string bulkSeparator = null,
            string metaSchemaName = null,
            string metaTableName = null,
            int? commandTimeout = null,
            int? bulkBatchSize = null,
            string appliedByTool = null,
            string appliedByToolVersion = null,
            string environment = null,
            string transactionMode = null
        )
        {
            //excludes all versions already executed
            var versionDirectories = _directoryService.GetDirectories(workspace, "v*.*")
                .Where(v => !appliedVersions.Contains(new DirectoryInfo(v).Name))
                .ToList();

            //exclude all versions greater than the target version
            if (!string.IsNullOrEmpty(targetVersion))
            {
                versionDirectories.RemoveAll(v =>
                {
                    var cv = new LocalVersion(new DirectoryInfo(v).Name);
                    var tv = new LocalVersion(targetVersion);
                    return string.Compare(cv.SemVersion, tv.SemVersion) == 1;
                });
            }

            //execute all sql scripts in the version folders
            if (versionDirectories.Any())
            {
                versionDirectories.Sort();
                versionDirectories.ForEach(versionDirectory =>
                {
                    if (!string.IsNullOrEmpty(transactionMode) && transactionMode.Equals(TRANSACTION_MODE.VERSION))
                    {
                        using (var internalConnection = _dataService.CreateConnection())
                        {
                            internalConnection.Open();
                            using (var internalTransaction = internalConnection.BeginTransaction())
                            {
                                try
                                {
                                    if (null != internalTransaction)
                                        _traceService.Info("Transaction created for current version. This version migration run will be executed in this dedicated connection and transaction context.");

                                    RunVersionScriptsInternal(internalConnection, internalTransaction, versionDirectory);
                                    internalTransaction.Commit();
                                }
                                catch (Exception)
                                {
                                    internalTransaction.Rollback();
                                    throw;
                                }
                            }
                        }
                    }
                    else
                    {
                        if (null == transaction)
                            _traceService.Info("Transaction is disabled for current session. This version migration run will be executed without explicit transaction context.");

                        RunVersionScriptsInternal(connection, transaction, versionDirectory);
                    }
                });
            }
            else
            {
                var connectionInfo = _dataService.GetConnectionInfo();
                _traceService.Info($"Target database is updated. No migration step executed at {connectionInfo.Database} on {connectionInfo.DataSource}.");
            }

            void RunVersionScriptsInternal(IDbConnection connection, IDbTransaction transaction, string versionDirectory)
            {
                try
                {
                    var versionName = new DirectoryInfo(versionDirectory).Name;

                    //run scripts in all sub-directories
                    var scriptSubDirectories = _directoryService.GetAllDirectories(versionDirectory, "*").ToList();
                    scriptSubDirectories.Sort();
                    scriptSubDirectories.ForEach(scriptSubDirectory =>
                    {
                        //run all scripts in the current version folder
                        RunSqlScripts(connection, transaction, transactionContext, versionName, workspace, scriptSubDirectory, metaSchemaName, metaTableName, tokens, commandTimeout, environment);

                        //import csv files into tables of the the same filename as the csv
                        RunBulkImport(connection, transaction, workspace, scriptSubDirectory, bulkSeparator, bulkBatchSize, commandTimeout, environment);
                    });

                    //run all scripts in the current version folder
                    RunSqlScripts(connection, transaction, transactionContext, versionName, workspace, versionDirectory, metaSchemaName, metaTableName, tokens, commandTimeout, environment);

                    //import csv files into tables of the the same filename as the csv
                    RunBulkImport(connection, transaction, workspace, versionDirectory, bulkSeparator, bulkBatchSize, commandTimeout, environment);

                    //update db version
                    _metadataService.InsertVersion(connection, transaction, versionName, transactionContext,
                        metaSchemaName: metaSchemaName,
                        metaTableName: metaTableName,
                        commandTimeout: commandTimeout,
                        appliedByTool: appliedByTool,
                        appliedByToolVersion: appliedByToolVersion);

                    _traceService.Info($"Completed migration to version {versionDirectory}");
                }
                finally
                {
                    //clear nontransactional context to ensure it is not applied on next version
                    transactionContext = null;
                }
            }
        }

        ///<inheritdoc/>
        public override void RunSqlScripts(
            IDbConnection connection,
            IDbTransaction transaction,
            TransactionContext transactionContext,
            string version,
            string workspace,
            string scriptDirectory,
            string metaSchemaName,
            string metaTableName,
            List<KeyValuePair<string, string>> tokens = null,
            int? commandTimeout = null,
            string environment = null,
            string appliedByTool = null,
            string appliedByToolVersion = null
        )
        {
            string currentScriptFile = null;
            try
            {
                //filter out scripts when environment code is used
                var sqlScriptFiles = _directoryService.GetFiles(scriptDirectory, "*.sql").ToList();
                sqlScriptFiles = _directoryService.FilterFiles(workspace, environment, sqlScriptFiles).ToList();
                _traceService.Info($"Found {sqlScriptFiles.Count} script files on {workspace}" + (sqlScriptFiles.Count > 0 ? Environment.NewLine : string.Empty) +
                       $"{string.Join(Environment.NewLine, sqlScriptFiles.Select(s => "  + " + new FileInfo(s).Name))}");

                //execute all script files in the version folder, we also make sure its sorted by file name
                sqlScriptFiles.Sort();
                sqlScriptFiles.ForEach(scriptFile =>
                {
                    currentScriptFile = scriptFile;

                    //in case the non-transactional failure is resolved, skip scripts
                    if (null != transactionContext
                        && transactionContext.ContinueAfterFailure.HasValue
                        && transactionContext.ContinueAfterFailure.Value
                        && !transactionContext.IsFailedScriptPathMatched)
                    {
                        //set failed script file as matched
                        if (string.Equals(scriptFile, transactionContext.LastKnownFailedScriptPath, StringComparison.InvariantCultureIgnoreCase))
                        {
                            transactionContext.SetFailedScriptPathMatch();
                        }
                        _traceService.Info($"Skipping script file {scriptFile} ...");
                    }
                    else //otherwise execute them
                    {
                        var sqlStatementRaw = _fileService.ReadAllText(scriptFile);
                        var sqlStatements = _dataService.BreakStatements(sqlStatementRaw)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();

                        sqlStatements.ForEach(sqlStatement =>
                        {
                            sqlStatement = _tokenReplacementService.Replace(tokens, sqlStatement);
                            _traceService.Debug($"Executing sql statement as part of : {scriptFile}");

                            _metadataService.ExecuteSql(
                                connection: connection,
                                commandText: sqlStatement,
                                transaction: transaction,
                                commandTimeout: commandTimeout,
                                traceService: _traceService);
                        });

                        _traceService.Info($"Executed script file {scriptFile}.");
                    }
                });
            }
            catch (Exception exception)
            {
                //try parse the known sql error and if not sucesfull, use the whole exception
                if (!_dataService.TryParseErrorFromException(exception, out string parsedExceptionMessage))
                    parsedExceptionMessage = exception.Message;

                //in case scripts are not executed within transaction, mark version as failed in database
                var configuration = _configurationService.GetConfiguration();
                if (configuration.TransactionMode == TRANSACTION_MODE.NONE)
                {
                    _metadataService.InsertVersion(connection, transaction, version, transactionContext,
                        metaSchemaName: metaSchemaName,
                        metaTableName: metaTableName,
                        commandTimeout: commandTimeout,
                        appliedByTool: appliedByTool,
                        appliedByToolVersion: appliedByToolVersion,
                        failedScriptPath: currentScriptFile,
                        failedScriptError: parsedExceptionMessage);
                }

                var transactionModeText = configuration.TransactionMode == TRANSACTION_MODE.NONE ? "not running in transaction" : "running in transaction";
                var suggestionText = configuration.TransactionMode == TRANSACTION_MODE.NONE ? MESSAGES.ManualResolvingAfterFailureMessage : MESSAGES.TransactionalAfterFailureMessage;
                var exceptionMessage = @$"Migration of version {version} was {transactionModeText} and has failed while attempting to execute script file {currentScriptFile} due to error ""{parsedExceptionMessage}"". {suggestionText}";
                throw new YuniqlMigrationException(exceptionMessage, exception);
            }
        }
    }
}
