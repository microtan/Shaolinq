// Copyright (c) 2007-2016 Thong Nguyen (tumtumtum@gmail.com)

using Shaolinq.Persistence;

namespace Shaolinq.Sqlite
{
	public static class SqliteConfiguration
	{
		public static DataAccessModelConfiguration Create(string connectionString)
		{
			return new DataAccessModelConfiguration
			{
				SqlDatabaseContextInfos = new SqlDatabaseContextInfo[]
       			{
       				new SqliteSqlDatabaseContextInfo
       				{
       					ConnectionString = connectionString
       				},
       			}
			};
		}

		public static DataAccessModelConfiguration Create(string fileName, string categories, bool useMonoData = false)
		{
			return new DataAccessModelConfiguration
			{
		       	SqlDatabaseContextInfos = new SqlDatabaseContextInfo[]
       			{
       				new SqliteSqlDatabaseContextInfo()
       				{
       					Categories = categories,
       					FileName = fileName,
						UseMonoData = useMonoData
       				},
       			}
			};
		}
	}
}
