﻿// Copyright (c) 2007-2014 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using Platform;
using Shaolinq.Persistence.Linq.Expressions;

namespace Shaolinq.Persistence.Linq
{
	public class SqlDataDefinitionExpressionBuilder
	{
		private readonly SqlDialect sqlDialect;
		private readonly SqlDataTypeProvider sqlDataTypeProvider;
		private readonly DataAccessModel model;
		private readonly string tableNamePrefix;
		private List<Expression> currentTableConstraints;
		private readonly SqlDataDefinitionBuilderFlags flags;
		
		private SqlDataDefinitionExpressionBuilder(SqlDialect sqlDialect, SqlDataTypeProvider sqlDataTypeProvider, DataAccessModel model, string tableNamePrefix, SqlDataDefinitionBuilderFlags flags)
		{
			this.model = model;
			this.sqlDialect = sqlDialect;
			this.tableNamePrefix = tableNamePrefix;
			this.flags = flags;
			this.sqlDataTypeProvider = sqlDataTypeProvider;

			this.currentTableConstraints = new List<Expression>();
		}

		private List<Expression> BuildColumnConstraints(PropertyDescriptor propertyDescriptor, string[] columnNames, PropertyDescriptor foreignKeyReferencingProperty)
		{
			var retval = new List<Expression>();

			if (foreignKeyReferencingProperty != null)
			{
				var valueRequiredAttribute = foreignKeyReferencingProperty.ValueRequiredAttribute;

				if (valueRequiredAttribute != null && valueRequiredAttribute.Required)
				{
					retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.NotNull));
				}
			}
			else
			{
				if (propertyDescriptor.PropertyType.IsNullableType() || !propertyDescriptor.PropertyType.IsValueType)
				{
					var valueRequiredAttribute = propertyDescriptor.ValueRequiredAttribute;

					if (valueRequiredAttribute != null && valueRequiredAttribute.Required)
					{
						retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.NotNull));
					}
				}
				else
				{
					retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.NotNull));
				}

				if (propertyDescriptor.IsPrimaryKey)
				{
					retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.PrimaryKey));
				}

				if (propertyDescriptor.IsAutoIncrement && propertyDescriptor.PropertyType.IsIntegerType(true))
				{
					retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.AutoIncrement));
				}

				if (propertyDescriptor.HasUniqueAttribute && propertyDescriptor.UniqueAttribute.Unique)
				{
					retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.Unique));
				}

				
				var defaultValueAttribute = propertyDescriptor.DefaultValueAttribute;

				if (defaultValueAttribute != null)
				{
					retval.Add(new SqlSimpleConstraintExpression(SqlSimpleConstraint.DefaultValue, null, defaultValueAttribute.Value));
				}
			}

			return retval;
		}

		private IEnumerable<Expression> BuildForeignKeyColumnDefinitions(PropertyDescriptor referencingProperty, ForeignKeyColumnInfo[] columnNamesAndReferencedTypeProperties)
		{
			var relatedPropertyTypeDescriptor = this.model.ModelTypeDescriptor.GetTypeDescriptor(referencingProperty.PropertyType);
			var referencedTableName = SqlQueryFormatter.PrefixedTableName(this.tableNamePrefix, relatedPropertyTypeDescriptor.PersistedName);

			var valueRequired = (referencingProperty.ValueRequiredAttribute != null && referencingProperty.ValueRequiredAttribute.Required);
			var supportsInlineForeignKeys = this.sqlDialect.SupportsFeature(SqlFeature.SupportsAndPrefersInlineForeignKeysWherePossible);

			foreach (var foreignKeyColumn in columnNamesAndReferencedTypeProperties)
			{
				var retval = (SqlColumnDefinitionExpression)this.BuildColumnDefinitions(foreignKeyColumn.KeyPropertyOnForeignType, foreignKeyColumn.ColumnName, referencingProperty).First();

				if (columnNamesAndReferencedTypeProperties.Length == 1 && supportsInlineForeignKeys)
				{
					var names = new ReadOnlyCollection<string>(new[] { foreignKeyColumn.KeyPropertyOnForeignType.PersistedName });
					var newConstraints = new List<Expression>(retval.ConstraintExpressions);
					var referencesColumnExpression = new SqlReferencesColumnExpression(referencedTableName, SqlColumnReferenceDeferrability.InitiallyDeferred, names, valueRequired ? SqlColumnReferenceAction.Restrict : SqlColumnReferenceAction.SetNull, SqlColumnReferenceAction.NoAction);

					newConstraints.Add(referencesColumnExpression);

					retval = new SqlColumnDefinitionExpression(retval.ColumnName, retval.ColumnType, newConstraints);
				}

				yield return retval;
			}

			if (columnNamesAndReferencedTypeProperties.Length > 1 || !supportsInlineForeignKeys)
			{
				var currentTableColumnNames = new ReadOnlyCollection<string>(columnNamesAndReferencedTypeProperties.Select(c => c.ColumnName).ToList());
				var referencedTableColumnNames = new ReadOnlyCollection<string>(columnNamesAndReferencedTypeProperties.Select(c => c.KeyPropertyOnForeignType.PersistedName).ToList());
				var referencesColumnExpression = new SqlReferencesColumnExpression(referencedTableName, SqlColumnReferenceDeferrability.InitiallyDeferred, referencedTableColumnNames, valueRequired ? SqlColumnReferenceAction.Restrict : SqlColumnReferenceAction.SetNull, SqlColumnReferenceAction.NoAction);
				var foreignKeyConstraint = new SqlForeignKeyConstraintExpression(null, currentTableColumnNames, referencesColumnExpression);

				currentTableConstraints.Add(foreignKeyConstraint);
			}
		}

		private IEnumerable<Expression> BuildColumnDefinitions(PropertyDescriptor propertyDescriptor, string columnName, PropertyDescriptor foreignKeyReferencingProperty)
		{
			if (columnName == null)
			{
				columnName = propertyDescriptor.PersistedName;
			}

			if (propertyDescriptor.PropertyType.IsDataAccessObjectType())
			{
				var foreignKeyColumn = QueryBinder.ExpandPropertyIntoForeignKeyColumns(this.model, propertyDescriptor);
				
				foreach (var result in this.BuildForeignKeyColumnDefinitions(propertyDescriptor, foreignKeyColumn))
				{
					yield return result;
				}

				yield break;
			}

			var sqlDataType = this.sqlDataTypeProvider.GetSqlDataType(propertyDescriptor.PropertyType);
			var columnDataTypeName = sqlDataType.GetSqlName(propertyDescriptor);
			var constraints = this.BuildColumnConstraints(propertyDescriptor, new[] { columnName }, foreignKeyReferencingProperty);

			yield return new SqlColumnDefinitionExpression(columnName, new SqlTypeExpression(columnDataTypeName, sqlDataType.IsUserDefinedType), constraints);
		}

		private IEnumerable<Expression> BuildRelatedColumnDefinitions(TypeDescriptor typeDescriptor)
		{
			foreach (var typeRelationshipInfo in typeDescriptor.GetRelationshipInfos())
			{
				if (typeRelationshipInfo.EntityRelationshipType == EntityRelationshipType.ChildOfOneToMany)
				{
					var foreignKeyColumns = QueryBinder.ExpandPropertyIntoForeignKeyColumns(this.model, typeRelationshipInfo.ReferencingProperty);

					foreach (var result in this.BuildForeignKeyColumnDefinitions(typeRelationshipInfo.ReferencingProperty, foreignKeyColumns))
					{
						yield return result;
					}
				}
			}
		}

		private Expression BuildCreateTableExpression(TypeDescriptor typeDescriptor)
		{
			var columnExpressions = new List<Expression>();

			currentTableConstraints = new List<Expression>();

			foreach (var propertyDescriptor in typeDescriptor.PersistedProperties)
			{
				columnExpressions.AddRange(this.BuildColumnDefinitions(propertyDescriptor, propertyDescriptor.PersistedName, null));
			}

			columnExpressions.AddRange(BuildRelatedColumnDefinitions(typeDescriptor));

			var tableName = SqlQueryFormatter.PrefixedTableName(this.tableNamePrefix, typeDescriptor.PersistedName);

			var primaryKeys = typeDescriptor.PersistedProperties.Where(c => c.IsPrimaryKey).ToList();

			if (primaryKeys.Count > 1)
			{
				var columnNames = primaryKeys.OrderBy(c => c.PrimaryKeyAttribute.CompositeOrder).SelectMany(c =>
				{
					if (c.PropertyType.IsDataAccessObjectType())
					{
						return QueryBinder.ExpandPropertyIntoForeignKeyColumns(this.model, c).Select(d => d.ColumnName).ToArray();
					}
					else
					{
						return new [] { c.PersistedName };
					}
				}).ToArray();

				var compositePrimaryKeyConstraint = new SqlSimpleConstraintExpression(SqlSimpleConstraint.PrimaryKey, columnNames);

				this.currentTableConstraints.Add(compositePrimaryKeyConstraint);
			}

			return new SqlCreateTableExpression(new SqlTableExpression(typeof(void), null, tableName), false, columnExpressions, this.currentTableConstraints);
		}

		private Expression BuildIndexExpression(SqlTableExpression table, string indexName, Tuple<IndexAttribute, PropertyDescriptor>[] properties)
		{
			var unique = properties.Select(c => c.Item1).Any(c => c.Unique);
			var lowercaseIndex = properties.Select(c => c.Item1).Any(c => c.LowercaseIndex);
			var indexType = properties.Select(c => c.Item1.IndexType).FirstOrDefault(c => c != IndexType.Default);

			var sorted = properties.OrderBy(c => c.Item1.CompositeOrder, Comparer<int>.Default);

			var indexedColumns = new List<SqlIndexedColumnExpression>();

			foreach (var attributeAndProperty in sorted)
			{
				if (attributeAndProperty.Item2.PropertyType.IsDataAccessObjectType())
				{
					foreach (var info in QueryBinder.ExpandPropertyIntoForeignKeyColumns(this.model, attributeAndProperty.Item2))
					{
						indexedColumns.Add(new SqlIndexedColumnExpression(new SqlColumnExpression(info.KeyPropertyOnForeignType.PropertyType, null, info.ColumnName), attributeAndProperty.Item1.SortOrder));
					}
				}
				else
				{
					indexedColumns.Add(new SqlIndexedColumnExpression(new SqlColumnExpression(attributeAndProperty.Item2.PropertyType, null, attributeAndProperty.Item2.PersistedName), attributeAndProperty.Item1.SortOrder));
				}
			}
				
			return new SqlCreateIndexExpression(indexName, table, unique, lowercaseIndex, indexType, false, new ReadOnlyCollection<SqlIndexedColumnExpression>(indexedColumns));
		}

		private IEnumerable<Expression> BuildCreateIndexExpressions(TypeDescriptor typeDescriptor)
		{
			var allIndexAttributes = typeDescriptor.PersistedProperties.Append(typeDescriptor.RelatedProperties).SelectMany(c => c.IndexAttributes.Select(d => new Tuple<IndexAttribute, PropertyDescriptor>(d, c)));

			var indexAttributesByName = allIndexAttributes.GroupBy(c => c.Item1.IndexName ?? typeDescriptor.PersistedName + "_" + c.Item2.PersistedName + "_idx").Sorted((x, y) => String.CompareOrdinal(x.Key, y.Key));

			var table = new SqlTableExpression(typeDescriptor.PersistedName);

			foreach (var group in indexAttributesByName)
			{
				var indexName = group.Key;

				var propertyDescriptors = group.ToArray();

				yield return this.BuildIndexExpression(table, indexName, propertyDescriptors);
			}
		}

		private Expression Build()
		{
			var expressions = new List<Expression>();

			if ((flags & SqlDataDefinitionBuilderFlags.BuildEnums) != 0)
			{
				foreach (var enumTypeDescriptor in this.model.ModelTypeDescriptor.GetPersistedEnumTypeDescriptors())
				{
					expressions.Add(BuildCreateEnumTypeExpression(enumTypeDescriptor));
				}
			}

			if ((flags & (SqlDataDefinitionBuilderFlags.BuildIndexes | SqlDataDefinitionBuilderFlags.BuildIndexes)) != 0)
			{
				foreach (var typeDescriptor in this.model.ModelTypeDescriptor.GetPersistedObjectTypeDescriptors())
				{
					expressions.Add(BuildCreateTableExpression(typeDescriptor));
					expressions.AddRange(BuildCreateIndexExpressions(typeDescriptor));
				}
			}
			else
			{
				if ((flags & (SqlDataDefinitionBuilderFlags.BuildIndexes)) != 0)
				{
					foreach (var typeDescriptor in this.model.ModelTypeDescriptor.GetPersistedObjectTypeDescriptors())
					{
						expressions.AddRange(BuildCreateIndexExpressions(typeDescriptor));
					}
				}

				if ((flags & (SqlDataDefinitionBuilderFlags.BuildTables)) != 0)
				{
					foreach (var typeDescriptor in this.model.ModelTypeDescriptor.GetPersistedObjectTypeDescriptors())
					{
						expressions.Add(BuildCreateTableExpression(typeDescriptor));
					}
				}
			}

			return new SqlStatementListExpression(new ReadOnlyCollection<Expression>(expressions));
		}

		private Expression BuildCreateEnumTypeExpression(EnumTypeDescriptor enumTypeDescriptor)
		{
			var sqlTypeExpression = new SqlTypeExpression(enumTypeDescriptor.Name);
			var asExpression = new SqlEnumDefinitionExpression(new ReadOnlyCollection<string>(enumTypeDescriptor.GetValues()));

			return new SqlCreateTypeExpression(sqlTypeExpression, asExpression, true);
		}

		public static Expression Build(SqlDataTypeProvider sqlDataTypeProvider, SqlDialect sqlDialect, DataAccessModel model, string tableNamePrefix, SqlDataDefinitionBuilderFlags flags)
		{
			var builder = new SqlDataDefinitionExpressionBuilder(sqlDialect, sqlDataTypeProvider, model, tableNamePrefix, flags);

			var retval = builder.Build();

			retval = SqlMultiColumnPrimaryKeyRemover.Remove(retval);

			return retval;
		}
	}
}
