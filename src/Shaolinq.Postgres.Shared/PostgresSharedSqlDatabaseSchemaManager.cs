﻿// Copyright (c) 2007-2014 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Data;
using Shaolinq.Persistence;
using Shaolinq.Persistence.Linq;

namespace Shaolinq.Postgres.Shared
{
	public class PostgresSharedSqlDatabaseSchemaManager
		: SqlDatabaseSchemaManager
	{
		public PostgresSharedSqlDatabaseSchemaManager(SqlDatabaseContext sqlDatabaseContext)
			: base(sqlDatabaseContext)
		{
		}

		protected override SqlDataDefinitionBuilderFlags GetBuilderFlags()
		{
			var retval = base.GetBuilderFlags();

			if (((PostgresSharedSqlDataTypeProvider)this.SqlDatabaseContext.SqlDataTypeProvider).NativeEnums)
			{
				retval |= SqlDataDefinitionBuilderFlags.BuildEnums;
			}

			return retval;
		}

		protected override bool CreateDatabaseOnly(bool overwrite)
		{
			var retval = false;
			var factory = this.SqlDatabaseContext.CreateDbProviderFactory();
			var databaseName = this.SqlDatabaseContext.DatabaseName;

			this.SqlDatabaseContext.DropAllConnections();

			using (var dbConnection = factory.CreateConnection())
			{
				dbConnection.ConnectionString = this.SqlDatabaseContext.ServerConnectionString;
				dbConnection.Open();

				IDbCommand command;

				if (overwrite)
				{
					var drop = false;

					using (command = dbConnection.CreateCommand())
					{
						command.CommandText = String.Format("SELECT datname FROM pg_database;");

						using (var reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								var s = reader.GetString(0);

								if (s.Equals(databaseName))
								{
									drop = true;

									break;
								}
							}
						}
					}

					if (drop)
					{
						using (command = dbConnection.CreateCommand())
						{
							command.CommandText = String.Concat("DROP DATABASE \"", databaseName, "\";");
							command.ExecuteNonQuery();
						}
					}

					using (command = dbConnection.CreateCommand())
					{
						command.CommandText = String.Concat("CREATE DATABASE \"", databaseName, "\" WITH ENCODING 'UTF8';");
						command.ExecuteNonQuery();
					}

					retval = true;
				}
				else
				{
					try
					{
						using (command = dbConnection.CreateCommand())
						{
							command.CommandText = String.Concat("CREATE DATABASE \"", databaseName, "\" WITH ENCODING 'UTF8';");
							command.ExecuteNonQuery();
						}

						retval = true;
					}
					catch
					{
						retval = false;
					}
				}
			}

			return retval;
		}
	}
}
