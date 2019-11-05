﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArdiLabs.Yuniql
{
    public class SqlServerDataService : IDataService
    {
        private readonly string _connectionString;

        public SqlServerDataService(string connectionString)
        {
            this._connectionString = connectionString;
        }

        public void ExecuteNonQuery(string connectionString, string sqlStatement)
        {
            TraceService.Info(connectionString);
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        public bool QuerySingleBool(string connectionString, string sqlStatement)
        {
            TraceService.Info(connectionString);
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            bool result = false;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result = Convert.ToBoolean(reader.GetValue(0));
                    }
                }
            }

            return result;
        }

        public string QuerySingleString(string connectionString, string sqlStatement)
        {
            string result = null;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result = reader.GetString(0);
                    }
                }
            }

            return result;
        }

        public void ExecuteNonQuery(IDbConnection activeConnection, string sqlStatement, IDbTransaction transaction = null)
        {
            TraceService.Info(activeConnection.ConnectionString);
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            var command = activeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = sqlStatement;
            command.CommandTimeout = 0;
            command.ExecuteNonQuery();
        }

        public int ExecuteScalar(IDbConnection activeConnection, string sqlStatement, IDbTransaction transaction = null)
        {
            TraceService.Info(activeConnection.ConnectionString);
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            var result = 0;

            var command = activeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = sqlStatement;
            command.CommandTimeout = 0;
            result = command.ExecuteNonQuery();

            return result;
        }

        public bool QuerySingleBool(IDbConnection activeConnection, string sqlStatement, IDbTransaction transaction = null)
        {
            TraceService.Info(activeConnection.ConnectionString);
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            bool result = false;
            var command = activeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = sqlStatement;
            command.CommandTimeout = 0;

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    result = Convert.ToBoolean(reader.GetValue(0));
                }
            }

            return result;
        }

        public string QuerySingleString(IDbConnection activeConnection, string sqlStatement, IDbTransaction transaction = null)
        {
            TraceService.Info(activeConnection.ConnectionString);
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            string result = null;

            var command = activeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = sqlStatement;
            command.CommandTimeout = 0;

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    result = reader.GetString(0);
                }
            }
            return result;
        }

        public bool IsTargetDatabaseExists()
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            var sqlStatement = $"SELECT ISNULL(database_id,0) FROM [sys].[databases] WHERE name = '{connectionStringBuilder.InitialCatalog}'";

            //check if database exists and auto-create when its not
            var masterConnectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            masterConnectionStringBuilder.InitialCatalog = "master";

            var result = QuerySingleBool(masterConnectionStringBuilder.ConnectionString, sqlStatement);

            return result;
        }

        public void CreateDatabase()
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            var sqlStatement = $"CREATE DATABASE {connectionStringBuilder.InitialCatalog};";

            var masterConnectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            masterConnectionStringBuilder.InitialCatalog = "master";

            ExecuteNonQuery(masterConnectionStringBuilder.ConnectionString, sqlStatement);
        }

        public bool IsTargetDatabaseConfigured()
        {
            var sqlStatement = $"SELECT ISNULL(object_id,0) FROM [sys].[tables] WHERE name = '__YuniqlDbVersion'";
            var result = QuerySingleBool(_connectionString, sqlStatement);

            return result;
        }

        public void ConfigureDatabase()
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            var sqlStatement = $@"
                    USE {connectionStringBuilder.InitialCatalog};

                    IF OBJECT_ID('[dbo].[__YuniqlDbVersion]') IS NULL 
                    BEGIN
	                    CREATE TABLE [dbo].[__YuniqlDbVersion](
		                    [Id] [SMALLINT] IDENTITY(1,1),
		                    [Version] [NVARCHAR](32) NOT NULL,
		                    [DateInsertedUtc] [DATETIME] NOT NULL,
		                    [LastUpdatedUtc] [DATETIME] NOT NULL,
		                    [LastUserId] [NVARCHAR](128) NOT NULL,
		                    [Artifact] [VARBINARY](MAX) NULL,
		                    CONSTRAINT [PK___YuniqlDbVersion] PRIMARY KEY CLUSTERED ([Id] ASC),
		                    CONSTRAINT [IX___YuniqlDbVersion] UNIQUE NONCLUSTERED ([Version] ASC)
	                    );

	                    ALTER TABLE [dbo].[__YuniqlDbVersion] ADD  CONSTRAINT [DF___YuniqlDbVersion_DateInsertedUtc]  DEFAULT (GETUTCDATE()) FOR [DateInsertedUtc];
	                    ALTER TABLE [dbo].[__YuniqlDbVersion] ADD  CONSTRAINT [DF___YuniqlDbVersion_LastUpdatedUtc]  DEFAULT (GETUTCDATE()) FOR [LastUpdatedUtc];
	                    ALTER TABLE [dbo].[__YuniqlDbVersion] ADD  CONSTRAINT [DF___YuniqlDbVersion_LastUserId]  DEFAULT (SUSER_SNAME()) FOR [LastUserId];
                    END                
            ";

            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        public string GetCurrentVersion()
        {
            var sqlStatement = $"SELECT TOP 1 Version FROM [dbo].[__YuniqlDbVersion] ORDER BY Id DESC;";
            var result = QuerySingleString(_connectionString, sqlStatement);

            return result;
        }

        public List<DbVersion> GetAllVersions()
        {
            var result = new List<DbVersion>();

            var sqlStatement = $"SELECT Id, Version, DateInsertedUtc, LastUserId FROM [dbo].[__YuniqlDbVersion] ORDER BY Version ASC;";
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{sqlStatement}");

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandType = CommandType.Text;
                command.CommandText = sqlStatement;
                command.CommandTimeout = 0;

                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var dbVersion = new DbVersion
                    {
                        Id = reader.GetInt16(0),
                        Version = reader.GetString(1),
                        DateInsertedUtc = reader.GetDateTime(2),
                        LastUserId = reader.GetString(3)
                    };
                    result.Add(dbVersion);
                }
            }

            return result;
        }

        public void UpdateVersion(IDbConnection activeConnection, IDbTransaction transaction, string version)
        {
            var incrementVersionSqlStatement = $"INSERT INTO [dbo].[__YuniqlDbVersion] (Version) VALUES ('{version}');";
            TraceService.Debug($"Executing sql statement: {Environment.NewLine}{incrementVersionSqlStatement}");

            var command = activeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = incrementVersionSqlStatement;
            command.CommandTimeout = 0;
            command.ExecuteNonQuery();
        }

        public List<string> BreakStatements(string sqlStatementRaw)
        {
            return Regex.Split(sqlStatementRaw, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
    }
}
