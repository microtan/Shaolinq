// Copyright (c) 2007-2014 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Runtime.InteropServices;
using Platform.Xml.Serialization;
using Shaolinq.Persistence;

namespace Shaolinq.Sqlite
{
	[XmlElement]
	public class SqliteSqlDatabaseContextInfo
		: SqlDatabaseContextInfo
	{
		private static class NativeMethods
		{
			internal const int SQLITE_CONFIG_SERIALIZED = 3;

			[DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_config")]
			internal static extern int sqlite3_config_int(int op, int value);
		}

		[XmlAttribute]
		public string FileName { get; set; }

		public override SqlDatabaseContext CreateSqlDatabaseContext(DataAccessModel model)
		{
			var useMonoData = Environment.GetEnvironmentVariable("SHAOLINQ_USE_MONO_DATA_SQLITE");

			if (!string.IsNullOrEmpty(useMonoData) && SqliteSqlDatabaseContext.IsRunningMono())
			{
				return SqliteMonoSqlDatabaseContext.Create(this, model);
			}
			else
			{
				if (SqliteSqlDatabaseContext.IsRunningMono())
				{
					try
					{
						NativeMethods.sqlite3_config_int(NativeMethods.SQLITE_CONFIG_SERIALIZED, 1);
					}
					catch (Exception)
					{
					}
				}		

				return SqliteOfficialsSqlDatabaseContext.Create(this, model);
			}
		}
	}
}