﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using NUnit.Framework;
using Shaolinq.Persistence.Linq;
using Shaolinq.Persistence.Linq.Expressions;

namespace Shaolinq.Tests
{
	[TestFixture("Sqlite")]
	[TestFixture("Postgres")]
	public class SqlFormatterTests
		: BaseTests
	{
		public SqlFormatterTests(string providerName)
			: base(providerName)
		{	
		}

		[Test]
		public void Test_DataDefinitionBuilder()
		{
			var dbConnection = this.model.GetCurrentDatabaseConnection(DatabaseReadMode.ReadWrite);
			var dataDefinitionExpressions = SqlDataDefinitionExpressionBuilder.Build(dbConnection.SqlDataTypeProvider, dbConnection.SqlDialect, this.model);

			var formatter = dbConnection.NewQueryFormatter(this.model, dbConnection.SqlDataTypeProvider, dbConnection.SqlDialect, dataDefinitionExpressions, SqlQueryFormatterOptions.Default);

			Console.WriteLine(formatter.Format().CommandText);
		}

		[Test]
		public void Test_Format_Create_Table_With_Table_Constraints()
		{
			var columnDefinitions = new List<Expression>
			{
				new SqlColumnDefinitionExpression("Column1", "INTEGER", new List<Expression> { new SqlSimpleConstraintExpression(SqlSimpleConstraint.Unique),  new SqlReferencesColumnExpression("Table2", SqlColumnReferenceDeferrability.InitiallyDeferred, new ReadOnlyCollection<string>(new [] { "Id"}), SqlColumnReferenceAction.NoAction, SqlColumnReferenceAction.SetNull)})
			};

			var constraints = new List<Expression>
			{
				new SqlSimpleConstraintExpression(SqlSimpleConstraint.Unique, new[] {"Column1"}),
				new SqlForeignKeyConstraintExpression("fkc", new ReadOnlyCollection<string>(new [] {"Column1"}), new SqlReferencesColumnExpression("Table2", SqlColumnReferenceDeferrability.InitiallyDeferred, new ReadOnlyCollection<string>(new [] { "Id"}), SqlColumnReferenceAction.NoAction, SqlColumnReferenceAction.NoAction))
			};

			var createTableExpression = new SqlCreateTableExpression("Table1", columnDefinitions, constraints);

			var formatter = new Sql92QueryFormatter(createTableExpression);

			Console.WriteLine(formatter.Format().CommandText);
		}
	}
}