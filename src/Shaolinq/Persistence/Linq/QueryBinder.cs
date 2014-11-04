﻿// Copyright (c) 2007-2014 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Shaolinq.Persistence.Linq.Expressions;
using Platform;
using Platform.Reflection;
using Shaolinq.Persistence.Linq.Optimizers;

namespace Shaolinq.Persistence.Linq
{
	public class QueryBinder
		: Platform.Linq.ExpressionVisitor
	{
		public DataAccessModel DataAccessModel { get; private set; }

		private int aliasCount;
		private bool isWithinClientSideCode;
		private readonly Type conditionType;
		private LambdaExpression extraCondition;
		private readonly Expression rootExpression;
		private List<SqlOrderByExpression> thenBys;
		private Stack<Expression> selectorPredicateStack = new Stack<Expression>();
		private readonly TypeDescriptorProvider typeDescriptorProvider;
		private readonly Dictionary<Expression, GroupByInfo> groupByMap;
		private readonly RelatedPropertiesJoinExpanderResults joinExpanderResults;
		private readonly Dictionary<ParameterExpression, Expression> expressionsByParameter;
		private readonly Dictionary<MemberInitExpression, SqlObjectReference> objectReferenceByMemberInit = new Dictionary<MemberInitExpression, SqlObjectReference>(MemberInitEqualityComparer.Default);

		protected void AddExpressionByParameter(ParameterExpression parameterExpression, Expression expression)
		{
			expressionsByParameter[parameterExpression] = expression;
		}

		private QueryBinder(DataAccessModel dataAccessModel, Expression rootExpression, Type conditionType, LambdaExpression extraCondition, RelatedPropertiesJoinExpanderResults joinExpanderResults)
		{
			this.conditionType = conditionType;
			this.DataAccessModel = dataAccessModel;
			this.rootExpression = rootExpression;
			this.extraCondition = extraCondition;
			this.joinExpanderResults = joinExpanderResults;
			this.typeDescriptorProvider = dataAccessModel.TypeDescriptorProvider;

			expressionsByParameter = new Dictionary<ParameterExpression, Expression>();
			groupByMap = new Dictionary<Expression, GroupByInfo>();
		}

		public static Expression Bind(DataAccessModel dataAccessModel, Expression expression, Type conditionType, LambdaExpression extraCondition)
		{
			expression = ConditionalMethodsToWhereConverter.Convert(expression);
			expression = QueryableIncludeExpander.Expand(expression);
			var joinExpanderResults = RelatedPropertiesJoinExpander.Expand(dataAccessModel, expression);

			expression = joinExpanderResults.ProcessedExpression;
			
			var queryBinder = new QueryBinder(dataAccessModel, expression, conditionType, extraCondition, joinExpanderResults);
			
			return queryBinder.Visit(expression);
		}

		public static bool RequiresColumnProjection(Expression expression)
		{
			switch (expression.NodeType)
			{
				case (ExpressionType)SqlExpressionType.Column:
				case (ExpressionType)SqlExpressionType.Subquery:
				case (ExpressionType)SqlExpressionType.AggregateSubquery:
				case (ExpressionType)SqlExpressionType.Aggregate:
				case (ExpressionType)SqlExpressionType.ObjectReference:
					return true;
				default:
					return false;
			}
		}

		internal static Expression StripQuotes(Expression expression)
		{
			while (expression.NodeType == ExpressionType.Quote)
			{
				expression = ((UnaryExpression)expression).Operand;
			}

			return expression;
		}


		public static ColumnInfo[] GetPrimaryKeyColumnInfos(TypeDescriptorProvider typeDescriptorProvider, TypeDescriptor typeDescriptor)
		{
			return GetPrimaryKeyColumnInfos(typeDescriptorProvider, typeDescriptor, (c, d) => true, (c, d) => true);
		}

		public static ColumnInfo[] GetPrimaryKeyColumnInfos(TypeDescriptorProvider typeDescriptorProvider, TypeDescriptor typeDescriptor, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include)
		{
			return GetPrimaryKeyColumnInfos(typeDescriptorProvider, typeDescriptor, follow, include, new List<PropertyDescriptor>(0));
		}

		protected static ColumnInfo[] GetPrimaryKeyColumnInfos(TypeDescriptorProvider typeDescriptorProvider, TypeDescriptor typeDescriptor, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include, List<PropertyDescriptor> visitedProperties)
		{
			return GetColumnInfos(typeDescriptorProvider, typeDescriptor.PrimaryKeyProperties, follow, include, new List<PropertyDescriptor>(0));
		}

		public static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, IEnumerable<PropertyDescriptor> properties)
		{
			return GetColumnInfos(typeDescriptorProvider, properties, (c, d) => true, (c, d) => true, new List<PropertyDescriptor>(0));
		}

		public static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, params PropertyDescriptor[] properties)
		{
			return GetColumnInfos(typeDescriptorProvider, properties, (c, d) => true, (c, d) => true, new List<PropertyDescriptor>(0));
		}

		public static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, IEnumerable<PropertyDescriptor> properties, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include)
		{
			return GetColumnInfos(typeDescriptorProvider, properties, follow, include, new List<PropertyDescriptor>(0));
		}

		protected static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, IEnumerable<PropertyDescriptor> properties, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include, List<PropertyDescriptor> visitedProperties, int depth = 0)
		{
			var retval = new List<ColumnInfo>();

			foreach (var property in properties)
			{
				if (property.PropertyType.IsDataAccessObjectType())
				{
					if (!follow(property, depth))
					{
						continue;
					}

					var foreignTypeDescriptor = typeDescriptorProvider.GetTypeDescriptor(property.PropertyType);

					var newVisited = new List<PropertyDescriptor>(visitedProperties.Count + 1);

					newVisited.AddRange(visitedProperties);
					newVisited.Add(property);

					foreach (var relatedColumnInfo in GetColumnInfos(typeDescriptorProvider, foreignTypeDescriptor.PrimaryKeyProperties, follow, include, newVisited, depth + 1))
					{
						retval.Add(new ColumnInfo
						{
							ForeignType = foreignTypeDescriptor,
							DefinitionProperty = relatedColumnInfo.DefinitionProperty,
							VisitedProperties = relatedColumnInfo.VisitedProperties
						});
					}

				}
				else
				{
					if (!include(property, depth))
					{
						continue;
					}

					retval.Add(new ColumnInfo
					{
						ForeignType = null,
						DefinitionProperty = property,
						VisitedProperties = visitedProperties.ToArray()
					});
				}
			}

			return retval.ToArray();
		}

		private string GetNextAlias()
		{
			return "T" + (aliasCount++);
		}

		private ProjectedColumns ProjectColumns(Expression expression, string newAlias, params string[] existingAliases)
		{
			return ColumnProjector.ProjectColumns(QueryBinder.RequiresColumnProjection, expression, newAlias, this.objectReferenceByMemberInit, existingAliases);
		}

		private Expression BindContains(Expression checkList, Expression checkItem)
		{
			const string columnName = "CONTAINS";

			var functionExpression = new SqlFunctionCallExpression(typeof(bool), SqlFunction.In, Visit(checkItem), Visit(checkList));

			var alias = this.GetNextAlias();
			var selectType = this.DataAccessModel.AssemblyBuildInfo.GetEnumerableType(typeof(bool));

			var select = new SqlSelectExpression
			(
				selectType,
				alias,
				new[] { new SqlColumnDeclaration(columnName, functionExpression) },
				null,
				null,
				null,
				false
			);

			return new SqlProjectionExpression(select, new SqlColumnExpression(typeof(bool), alias, columnName), null);
		}

		private Expression BindFirst(Expression source, SelectFirstType selectFirstType)
		{
			int limit;

			var projection = this.VisitSequence(source);
			var select = projection.Select;
			var alias = this.GetNextAlias();
			var pc = ProjectColumns(projection.Projector, alias, projection.Select.Alias);

			switch (selectFirstType)
			{
				case SelectFirstType.Single:
				case SelectFirstType.SingleOrDefault:
					limit = 2;
					break;
				default:
					limit = 1;
					break;
			}

			return new SqlProjectionExpression(new SqlSelectExpression(select.Type, alias, pc.Columns, projection.Select, null, null, null, false, null, Expression.Constant(limit), select.ForUpdate), pc.Projector, null, false, selectFirstType, projection.DefaultValueExpression, projection.IsDefaultIfEmpty);
		}

		private Expression BindTake(Expression source, Expression take)
		{
			var projection = this.VisitSequence(source);

			take = this.Visit(take);

			var select = projection.Select;

			var alias = this.GetNextAlias();

			var pc = ProjectColumns(projection.Projector, alias, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(select.Type, alias, pc.Columns, projection.Select, null, null, null, false, null, take, select.ForUpdate), pc.Projector, null);
		}

		private Expression BindSkip(Expression source, Expression skip)
		{
			var projection = this.VisitSequence(source);

			skip = this.Visit(skip);

			var select = projection.Select;
			var alias = this.GetNextAlias();
			var pc = ProjectColumns(projection.Projector, alias, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(select.Type, alias, pc.Columns, projection.Select, null, null, null, false, skip, null, select.ForUpdate), pc.Projector, null);
		}

		protected override Expression VisitBinary(BinaryExpression binaryExpression)
		{
			if (this.isWithinClientSideCode)
			{
				return base.VisitBinary(binaryExpression);
			}

			Expression left, right;

			if (binaryExpression.Left.Type == typeof(string) && binaryExpression.Right.Type == typeof(string))
			{
				if (binaryExpression.NodeType == ExpressionType.Add)
				{
					left = Visit(binaryExpression.Left);
					right = Visit(binaryExpression.Right);

					return new SqlFunctionCallExpression(binaryExpression.Type, SqlFunction.Concat, left, right);
				}
			}

			if ((binaryExpression.NodeType == ExpressionType.GreaterThan
				|| binaryExpression.NodeType == ExpressionType.GreaterThanOrEqual
				|| binaryExpression.NodeType == ExpressionType.LessThan
				|| binaryExpression.NodeType == ExpressionType.LessThanOrEqual)
				&& binaryExpression.Left.NodeType == ExpressionType.Call)
			{
				var methodCallExpression = (MethodCallExpression)binaryExpression.Left;
				
				if (methodCallExpression.Method.Name == "CompareTo" && methodCallExpression.Arguments.Count == 1 && methodCallExpression.Method.ReturnType == typeof(int)
					&& binaryExpression.Right.NodeType == ExpressionType.Constant && ((ConstantExpression)binaryExpression.Right).Value.Equals(0))
				{
					return new SqlFunctionCallExpression(typeof(bool), SqlFunction.CompareObject, Expression.Constant(binaryExpression.NodeType), Visit(methodCallExpression.Object), Visit(methodCallExpression.Arguments[0]));
				}
			}

			if (binaryExpression.NodeType == ExpressionType.NotEqual
				|| binaryExpression.NodeType == ExpressionType.Equal)
			{
				var function = binaryExpression.NodeType == ExpressionType.NotEqual ? SqlFunction.IsNotNull : SqlFunction.IsNull;

				var leftConstantExpression = binaryExpression.Left as ConstantExpression;
				var rightConstantExpression = binaryExpression.Right as ConstantExpression;

				if (rightConstantExpression != null)
				{
					if (rightConstantExpression.Value == null)
					{
						if (leftConstantExpression == null || leftConstantExpression.Value != null)
						{
							return new SqlFunctionCallExpression(binaryExpression.Type, function, this.Visit(binaryExpression.Left));
						}
					}
				}

				if (leftConstantExpression != null)
				{
					if (leftConstantExpression.Value == null)
					{
						if (rightConstantExpression == null || rightConstantExpression.Value != null)
						{
							return new SqlFunctionCallExpression(binaryExpression.Type, function, this.Visit(binaryExpression.Right));
						}
					}
				}
			}

			if (binaryExpression.NodeType == ExpressionType.Coalesce)
            {
                left = Visit(binaryExpression.Left);
                right = Visit(binaryExpression.Right);

                return new SqlFunctionCallExpression(binaryExpression.Type, SqlFunction.Coalesce, new[] { left, right });
            }

			left = Visit(binaryExpression.Left);
			right = Visit(binaryExpression.Right);

			if (left.NodeType == ExpressionType.MemberInit)
			{
				left = this.objectReferenceByMemberInit[(MemberInitExpression)left];
			}

			if (right.NodeType == ExpressionType.MemberInit)
			{
				right = this.objectReferenceByMemberInit[(MemberInitExpression)right];
			}
			
			var conversion = Visit(binaryExpression.Conversion);

			if (left != binaryExpression.Left || right != binaryExpression.Right || conversion != binaryExpression.Conversion)
			{
				if (binaryExpression.NodeType == ExpressionType.Coalesce)
				{
					return Expression.Coalesce(left, right, conversion as LambdaExpression);
				}

				if (left.NodeType == (ExpressionType)SqlExpressionType.ObjectReference && right.NodeType == (ExpressionType)SqlExpressionType.Projection)
				{
					var objectOperandExpression = (SqlObjectReference)left;
					var tupleExpression = new SqlTupleExpression(objectOperandExpression.Bindings.OfType<MemberAssignment>().Select(c => c.Expression));
					var selector = MakeSelectorForPrimaryKeys(left.Type, tupleExpression.Type);
					var rightWithSelect = BindSelectForPrimaryKeyProjection(tupleExpression.Type, (SqlProjectionExpression)right, selector, false);

					return Expression.MakeBinary(binaryExpression.NodeType, tupleExpression, rightWithSelect, binaryExpression.IsLiftedToNull, binaryExpression.Method);
				}
				else if (left.NodeType == (ExpressionType)SqlExpressionType.Projection && right.NodeType == (ExpressionType)SqlExpressionType.ObjectReference)
				{
					var objectOperandExpression = (SqlObjectReference)right;
					var tupleExpression = new SqlTupleExpression(objectOperandExpression.Bindings.OfType<MemberAssignment>().Select(c => c.Expression));
					var selector = MakeSelectorForPrimaryKeys(right.Type, tupleExpression.Type);
					var leftWithSelect = BindSelectForPrimaryKeyProjection(tupleExpression.Type, (SqlProjectionExpression)right, selector, false);

					return Expression.MakeBinary(binaryExpression.NodeType, leftWithSelect, tupleExpression, binaryExpression.IsLiftedToNull, binaryExpression.Method);
				}
				else
				{
					return Expression.MakeBinary(binaryExpression.NodeType, left, right, binaryExpression.IsLiftedToNull, binaryExpression.Method);
				}
			}

			return binaryExpression;
		}

		private LambdaExpression MakeSelectorForPrimaryKeys(Type objectType,  Type returnType)
		{
			var parameter = Expression.Parameter(objectType);
			var constructor = returnType.GetConstructor(Type.EmptyTypes);
			var newExpression = Expression.New(constructor);

			var bindings = new List<MemberBinding>();
			var typeDescriptor = this.DataAccessModel.GetTypeDescriptor(objectType);

			var itemNumber = 1;

			foreach (var property in typeDescriptor.PrimaryKeyProperties)
			{
				var itemProperty = returnType.GetProperty("Item" + itemNumber, BindingFlags.Instance | BindingFlags.Public);
				bindings.Add(Expression.Bind(itemProperty, Expression.Property(parameter, property.PropertyName)));

				itemNumber++;
			}

			var body = Expression.MemberInit(newExpression, bindings);

			return Expression.Lambda(body, parameter);
		}

		private Expression BindSelectForPrimaryKeyProjection(Type resultType, SqlProjectionExpression projection, LambdaExpression selector, bool forUpdate)
		{
			Expression expression;
			var oldIsWithinClientSideCode = this.isWithinClientSideCode;
			
			AddExpressionByParameter(selector.Parameters[0], projection.Projector);

			this.isWithinClientSideCode = true;

			try
			{
				expression = this.Visit(selector.Body);
			}
			finally
			{
				this.isWithinClientSideCode = oldIsWithinClientSideCode;
			}

			var alias = this.GetNextAlias();
			var pc = ProjectColumns(expression, alias, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, null, null, forUpdate || projection.Select.ForUpdate), pc.Projector, null);
		}

		internal static Expression StripNullCheck(Expression expression)
		{
			if (expression.NodeType == ExpressionType.Conditional)
			{
				var conditional = (ConditionalExpression)expression;

				if (conditional.IfTrue.NodeType == ExpressionType.Constant && ((ConstantExpression)conditional.IfTrue).Value == null)
				{
					return conditional.IfFalse;
				}
			}

			return expression;
		}

		protected virtual Expression BindJoin(Type resultType, Expression outerSource, Expression innerSource, LambdaExpression outerKey, LambdaExpression innerKey, LambdaExpression resultSelector)
		{
			var outerProjection = (SqlProjectionExpression)this.Visit(outerSource);
			var innerProjection = (SqlProjectionExpression)this.Visit(innerSource);

			AddExpressionByParameter(outerKey.Parameters[0], outerProjection.Projector);
			var outerKeyExpr = StripNullCheck(this.Visit(outerKey.Body));
			AddExpressionByParameter(innerKey.Parameters[0], innerProjection.Projector);
			var innerKeyExpression = StripNullCheck(this.Visit(innerKey.Body));
			
			if (outerKeyExpr.NodeType == ExpressionType.MemberInit)
			{
				outerKeyExpr = this.objectReferenceByMemberInit[(MemberInitExpression)outerKeyExpr];
			}
			
			if (innerKeyExpression.NodeType == ExpressionType.MemberInit)
			{
				innerKeyExpression = this.objectReferenceByMemberInit[(MemberInitExpression)innerKeyExpression];
			}
			
			AddExpressionByParameter(resultSelector.Parameters[0], outerProjection.Projector);
			AddExpressionByParameter(resultSelector.Parameters[1], innerProjection.Projector);

			var resultExpr = this.Visit(resultSelector.Body);

			SqlJoinType joinType;

			if (outerProjection.IsDefaultIfEmpty && innerProjection.IsDefaultIfEmpty)
			{
				joinType = SqlJoinType.OuterJoin;
			}
			else if (outerProjection.IsDefaultIfEmpty && !innerProjection.IsDefaultIfEmpty)
			{
				joinType = SqlJoinType.RightJoin;
			}
			else if (!outerProjection.IsDefaultIfEmpty && innerProjection.IsDefaultIfEmpty)
			{
				joinType = SqlJoinType.LeftJoin;
			}
			else
			{
				joinType = SqlJoinType.InnerJoin;
			}

			var join = new SqlJoinExpression(resultType, joinType, outerProjection.Select, innerProjection.Select, Expression.Equal(outerKeyExpr, innerKeyExpression));
			
			var alias = this.GetNextAlias();

			var projectedColumns = ProjectColumns(resultExpr, alias, outerProjection.Select.Alias, innerProjection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, join, null, null, outerProjection.Select.ForUpdate || innerProjection.Select.ForUpdate), projectedColumns.Projector, null);
		}

		protected virtual Expression BindSelectMany(Type resultType, Expression source, LambdaExpression collectionSelector, LambdaExpression resultSelector)
		{
			SqlJoinType joinType;
			ProjectedColumns projectedColumns; 
			var projection = (SqlProjectionExpression)this.Visit(source);
			AddExpressionByParameter(collectionSelector.Parameters[0], projection.Projector);
			var selector = Evaluator.PartialEval(this.DataAccessModel, collectionSelector.Body);
			var collectionProjection = (SqlProjectionExpression)this.Visit(selector);
			
			if (IsTable(selector.Type))
			{
				joinType = SqlJoinType.CrossJoin;
			}
			else
			{
				throw new NotSupportedException();
			}

			var join = new SqlJoinExpression(resultType, joinType, projection.Select, collectionProjection.Select, null);

			var alias = this.GetNextAlias();
            
			if (resultSelector == null)
			{
				projectedColumns = ProjectColumns(collectionProjection.Projector, alias, projection.Select.Alias, collectionProjection.Select.Alias);
			}
			else
			{
				AddExpressionByParameter(resultSelector.Parameters[0], projection.Projector);
				AddExpressionByParameter(resultSelector.Parameters[1], collectionProjection.Projector);
				
				var resultExpression = this.Visit(resultSelector.Body);

				projectedColumns = ProjectColumns(resultExpression, alias, projection.Select.Alias, collectionProjection.Select.Alias);				
			}

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, join, null, null, false), projectedColumns.Projector, null);
		}

		protected virtual Expression BindGroupBy(Expression source, LambdaExpression keySelector, LambdaExpression elementSelector, LambdaExpression resultSelector)
		{
			var projection = this.VisitSequence(source);

			AddExpressionByParameter(keySelector.Parameters[0], projection.Projector);
			
			var keyExpression = this.Visit(keySelector.Body);

			var elementExpression = projection.Projector;

			if (elementSelector != null)
			{
				AddExpressionByParameter(elementSelector.Parameters[0], projection.Projector);
				elementExpression = this.Visit(elementSelector.Body);
			}

			// Use ProjectColumns to get group-by expressions from key expression
			var keyProjection = ProjectColumns(keyExpression, projection.Select.Alias, projection.Select.Alias);
			var groupExprs = new[] { keyExpression };

			// Make duplicate of source query as basis of element subquery by visiting the source again
			var subqueryBasis = this.VisitSequence(source);

			// Recompute key columns for group expressions relative to subquery (need these for doing the correlation predicate)
			AddExpressionByParameter(keySelector.Parameters[0], subqueryBasis.Projector);
			var subqueryKey = this.Visit(keySelector.Body);

			// Ise same projection trick to get group by expressions based on subquery
			var subQueryProjectedColumns = ProjectColumns(subqueryKey, subqueryBasis.Select.Alias, subqueryBasis.Select.Alias);
			var subqueryGroupExprs = new[] { subqueryKey };// CHANGED TO ALLOW FUNCTION CALL GROUPBY subQueryProjectedColumns.Columns.Select(c => c.Expression);
			var subqueryCorrelation = BuildPredicateWithNullsEqual(subqueryGroupExprs, groupExprs);

			// Compute element based on duplicated subquery
			var subqueryElemExpr = subqueryBasis.Projector;

			if (elementSelector != null)
			{
				AddExpressionByParameter(elementSelector.Parameters[0], subqueryBasis.Projector);
				subqueryElemExpr = this.Visit(elementSelector.Body);
			}

			// Build subquery that projects the desired element

			var elementAlias = this.GetNextAlias();

			var elementProjectedColumns = ProjectColumns(subqueryElemExpr, elementAlias, subqueryBasis.Select.Alias);
			
			var elementSubquery = new SqlProjectionExpression
			(
				new SqlSelectExpression
				(
					TypeHelper.GetSequenceType(subqueryElemExpr.Type),
					elementAlias,
					elementProjectedColumns.Columns,
					subqueryBasis.Select,
					subqueryCorrelation,
					null,
					subqueryBasis.Select.ForUpdate
				),
				elementProjectedColumns.Projector,
				null
			);

			var alias = this.GetNextAlias();

			// Make it possible to tie aggregates back to this group by
			var info = new GroupByInfo(alias, elementExpression);

			this.groupByMap.Add(elementSubquery, info);

			Expression resultExpression;

			if (resultSelector != null)
			{
				var saveGroupElement = this.currentGroupElement;

				this.currentGroupElement = elementSubquery;

				// Compute result expression based on key & element-subquery
				AddExpressionByParameter(resultSelector.Parameters[0], keyProjection.Projector);
				AddExpressionByParameter(resultSelector.Parameters[1], elementSubquery);
				resultExpression = this.Visit(resultSelector.Body);

				this.currentGroupElement = saveGroupElement;
			}
			else
			{
				// Result must be IGrouping<K,E>
				resultExpression = Expression.New(typeof(Grouping<,>).MakeGenericType(keyExpression.Type, subqueryElemExpr.Type).GetConstructors()[0], new Expression[] { keyExpression, elementSubquery });
			}

			var pc = ProjectColumns(resultExpression, alias, projection.Select.Alias);

			// Make it possible to tie aggregates back to this Group By

			var projectedElementSubquery = ((NewExpression)pc.Projector).Arguments[1];

			this.groupByMap.Add(projectedElementSubquery, info);

			return new SqlProjectionExpression
			(
				new SqlSelectExpression
				(
					TypeHelper.GetSequenceType(resultExpression.Type),
					alias,
					pc.Columns,
					projection.Select,
					null,
					null,
					groupExprs,
					false, null, null, projection.Select.ForUpdate
				),
				pc.Projector,
				null
			);
		}

		private static Expression BuildPredicateWithNullsEqual(IEnumerable<Expression> source1, IEnumerable<Expression> source2)
		{
			var enumerator1 = source1.GetEnumerator();
			var enumerator2 = source2.GetEnumerator();

			Expression result = null;

			while (enumerator1.MoveNext() && enumerator2.MoveNext())
			{
				var compare = Expression.Or
				(
					Expression.And
					(
						new SqlFunctionCallExpression(typeof(bool),
						SqlFunction.IsNull, enumerator1.Current),
						new SqlFunctionCallExpression(typeof(bool), SqlFunction.IsNull, enumerator2.Current)
					),
					Expression.Equal(enumerator1.Current, enumerator2.Current)
				);

				result = (result == null) ? compare : Expression.And(result, compare);
			}

			return result;
		}

		protected virtual Expression BindGroupJoin(MethodInfo groupJoinMethod, Expression outerSource, Expression innerSource, LambdaExpression outerKey, LambdaExpression innerKey, LambdaExpression resultSelector)
		{
			var args = groupJoinMethod.GetGenericArguments();

			var outerProjection = this.VisitSequence(outerSource);

			this.expressionsByParameter[outerKey.Parameters[0]] = outerProjection.Projector;
			var predicateLambda = Expression.Lambda(Expression.Equal(innerKey.Body, outerKey.Body), innerKey.Parameters[0]);
			var callToWhere = Expression.Call(typeof(Enumerable), "Where", new[] { args[1] }, innerSource, predicateLambda);
			var group = this.Visit(callToWhere);

			this.expressionsByParameter[resultSelector.Parameters[0]] = outerProjection.Projector;
			this.expressionsByParameter[resultSelector.Parameters[1]] = group;
			var resultExpr = this.Visit(resultSelector.Body);

			var alias = this.GetNextAlias();
			var pc = this.ProjectColumns(resultExpr, alias, outerProjection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression( outerProjection.Select.Type, alias, pc.Columns, outerProjection.Select, null, null, false), pc.Projector, null);
		}

		public static LambdaExpression GetLambda(Expression e)
		{
			while (e.NodeType == ExpressionType.Quote)
			{
				e = ((UnaryExpression)e).Operand;
			}

			if (e.NodeType == ExpressionType.Constant)
			{
				return ((ConstantExpression)e).Value as LambdaExpression;
			}

			return e as LambdaExpression;
		}

		protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
		{
			Expression result;

			if (methodCallExpression.Method.DeclaringType == typeof(Queryable)
				|| methodCallExpression.Method.DeclaringType == typeof(Enumerable)
				|| methodCallExpression.Method.DeclaringType == typeof(QueryableExtensions))
            {
	            switch (methodCallExpression.Method.Name)
	            {
	            case "Where":
					this.selectorPredicateStack.Push(methodCallExpression);
		            result = this.BindWhere(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), false);
		            this.selectorPredicateStack.Pop();
					return result;
	            case "WhereForUpdate":
		            this.selectorPredicateStack.Push(methodCallExpression);
		            result = this.BindWhere(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), true);
					this.selectorPredicateStack.Pop();
					return result;
	            case "Select":
					this.selectorPredicateStack.Push(methodCallExpression);
		            result = this.BindSelect(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), false);
		            this.selectorPredicateStack.Pop();
					return result;
	            case "SelectForUpdate":
					this.selectorPredicateStack.Push(methodCallExpression);
		            result = this.BindSelect(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), true);
					this.selectorPredicateStack.Pop();
					return result;
	            case "OrderBy":
					this.selectorPredicateStack.Push(methodCallExpression);
		            result = this.BindOrderBy(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), OrderType.Ascending);
		            this.selectorPredicateStack.Pop();
					return result;
	            case "OrderByDescending":
		            return this.BindOrderBy(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), OrderType.Descending);
	            case "ThenBy":
		            return this.BindThenBy(methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), OrderType.Ascending);
	            case "ThenByDescending":
		            return this.BindThenBy(methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), OrderType.Descending);
	            case "GroupJoin":
		            if (methodCallExpression.Arguments.Count == 5)
		            {
			            return this.BindGroupJoin(methodCallExpression.Method, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], GetLambda(methodCallExpression.Arguments[2]), GetLambda(methodCallExpression.Arguments[3]), GetLambda(methodCallExpression.Arguments[4]));
		            }
		            break;
	            case "GroupBy":
					this.selectorPredicateStack.Push(methodCallExpression);
		            if (methodCallExpression.Arguments.Count == 2)
		            {
			            result = this.BindGroupBy
						(
							methodCallExpression.Arguments[0],
							(LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]),
							null,
							null
						);
		            }
		            else if (methodCallExpression.Arguments.Count == 3)
		            {
			            result = this.BindGroupBy
						(
							methodCallExpression.Arguments[0],
							(LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]),
							(LambdaExpression)StripQuotes(methodCallExpression.Arguments[2]),
							null
						);
		            }
		            else if (methodCallExpression.Arguments.Count == 4)
		            {
			            result = this.BindGroupBy
						(
							methodCallExpression.Arguments[0],
							(LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]),
							(LambdaExpression)StripQuotes(methodCallExpression.Arguments[2]),
							(LambdaExpression)StripQuotes(methodCallExpression.Arguments[3])
						);
		            }
		            else
		            {
						break;
		            }
		            this.selectorPredicateStack.Pop();
					return result;
	            case "Count":
	            case "Min":
	            case "Max":
	            case "Sum":
	            case "Average":
		            if (methodCallExpression.Arguments.Count == 1)
		            {
			            return this.BindAggregate(methodCallExpression.Arguments[0], methodCallExpression.Method, null, methodCallExpression == this.rootExpression);
		            }
		            else if (methodCallExpression.Arguments.Count == 2)
		            {
			            var selector = (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]);

			            return this.BindAggregate(methodCallExpression.Arguments[0], methodCallExpression.Method, selector, methodCallExpression == this.rootExpression);
		            }
		            break;
	            case "Distinct":
		            return this.BindDistinct(methodCallExpression.Type, methodCallExpression.Arguments[0]);
	            case "Join":
		            return this.BindJoin(methodCallExpression.Type, methodCallExpression.Arguments[0],
		                                       methodCallExpression.Arguments[1],
		                                       (LambdaExpression)StripQuotes(methodCallExpression.Arguments[2]),
		                                       (LambdaExpression)StripQuotes(methodCallExpression.Arguments[3]),
		                                       (LambdaExpression)StripQuotes(methodCallExpression.Arguments[4]));
	            case "SelectMany":
		            this.selectorPredicateStack.Push(methodCallExpression);
					if (methodCallExpression.Arguments.Count == 2)
		            {
			            result = this.BindSelectMany(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), null);
		            }
		            else if (methodCallExpression.Arguments.Count == 3)
		            {
			            result = this.BindSelectMany(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]), (LambdaExpression)StripQuotes(methodCallExpression.Arguments[2]));
		            }
		            else
		            {
			            this.selectorPredicateStack.Pop();
						break;
		            }
					this.selectorPredicateStack.Pop();
		            return result;
	            case "Skip":
		            if (methodCallExpression.Arguments.Count == 2)
		            {
			            return this.BindSkip(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]);
		            }
		            break;
	            case "Take":
		            if (methodCallExpression.Arguments.Count == 2)
		            {
			            return this.BindTake(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]);
		            }
		            break;
	            case "First":
		            if (methodCallExpression.Arguments.Count == 1)
		            {
			            var retval = this.BindFirst(methodCallExpression.Arguments[0], SelectFirstType.First);

			            return retval;
		            }
		            else if (methodCallExpression.Arguments.Count == 2)
		            {
						var where = Expression.Call(null, MethodInfoFastRef.QueryableWhereMethod.MakeGenericMethod(methodCallExpression.Type), methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]));

			            return this.BindFirst(where, SelectFirstType.First);
		            }
		            break;
	            case "FirstOrDefault":
		            if (methodCallExpression.Arguments.Count == 1)
		            {
			            return this.BindFirst(methodCallExpression.Arguments[0], SelectFirstType.FirstOrDefault);
		            }
		            else if (methodCallExpression.Arguments.Count == 2)
		            {
						var where = Expression.Call(null, MethodInfoFastRef.QueryableWhereMethod.MakeGenericMethod(methodCallExpression.Type), methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]));

			            return this.BindFirst(where, SelectFirstType.FirstOrDefault);
		            }
		            break;
	            case "Single":
		            if (methodCallExpression.Arguments.Count == 1)
		            {
			            return this.BindFirst(methodCallExpression.Arguments[0], SelectFirstType.Single);
		            }
		            else if (methodCallExpression.Arguments.Count == 2)
		            {
						var where = Expression.Call(null, MethodInfoFastRef.QueryableWhereMethod.MakeGenericMethod(methodCallExpression.Type), methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]));

			            return this.BindFirst(where, SelectFirstType.Single);
		            }
		            break;
	            case "SingleOrDefault":
		            if (methodCallExpression.Arguments.Count == 1)
		            {
			            return this.BindFirst(methodCallExpression.Arguments[0], SelectFirstType.SingleOrDefault);
		            }
		            else if (methodCallExpression.Arguments.Count == 2)
		            {
						var where = Expression.Call(null, MethodInfoFastRef.QueryableWhereMethod.MakeGenericMethod(methodCallExpression.Type), methodCallExpression.Arguments[0], (LambdaExpression)StripQuotes(methodCallExpression.Arguments[1]));

			            return this.BindFirst(where, SelectFirstType.SingleOrDefault);
		            }
		            break;
	            case "DefaultIfEmpty":
		            if (methodCallExpression.Arguments.Count == 1)
		            {
			            var projectionExpression = (SqlProjectionExpression)this.Visit(methodCallExpression.Arguments[0]);

			            return projectionExpression.ToDefaultIfEmpty(null);
		            }
		            else if (methodCallExpression.Arguments.Count == 2)
		            {
			            var projectionExpression = (SqlProjectionExpression)this.Visit(methodCallExpression.Arguments[0]);

			            return projectionExpression.ToDefaultIfEmpty(methodCallExpression.Arguments[1]);
		            }
		            else
		            {
			            throw new NotSupportedException(methodCallExpression.ToString());
		            }
	            case "Contains":
		            if (methodCallExpression.Arguments.Count == 2)
		            {
			            return this.BindContains(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]);
		            }
		            break;
	            }

	            throw new NotSupportedException(String.Format("Linq function \"{0}\" is not supported", methodCallExpression.Method.Name));
			}
			else if (methodCallExpression.Method.DeclaringType == typeof(DataAccessObjectsQueryableExtensions))
			{
				switch (methodCallExpression.Method.Name)
				{
					case "DeleteWhere":
						if (methodCallExpression.Arguments.Count == 2)
						{
							return this.BindDelete(methodCallExpression.Arguments[0], (LambdaExpression)(StripQuotes(methodCallExpression.Arguments[1])));
						}
						break;
				}
			}
			else if (methodCallExpression.Method.DeclaringType == typeof(DefaultSqlTransactionalCommandsContext))
			{
				switch (methodCallExpression.Method.Name)
				{
					case "DeleteHelper":
						if (methodCallExpression.Arguments.Count == 2)
						{
							return this.BindDelete(methodCallExpression.Arguments[0], (LambdaExpression)(StripQuotes(methodCallExpression.Arguments[1])));
						}
						break;
				}
			}

			if (typeof(IList).IsAssignableFrom(methodCallExpression.Method.DeclaringType)
				|| typeof(ICollection).IsAssignableFrom(methodCallExpression.Method.DeclaringType)
				|| typeof(ICollection<>).IsAssignableFromIgnoreGenericParameters(methodCallExpression.Method.DeclaringType))
			{
				switch (methodCallExpression.Method.Name)
				{
					case "Contains":
						if (methodCallExpression.Arguments.Count == 1)
						{
							return this.BindContains(methodCallExpression.Object, methodCallExpression.Arguments[0]);
						}
						break;
				}
			}
			else if (!this.isWithinClientSideCode && methodCallExpression.Method == MethodInfoFastRef.StringExtensionsIsLikeMethodInfo)
			{
				var operand1 = Visit(methodCallExpression.Arguments[0]);
				var operand2 = Visit(methodCallExpression.Arguments[1]);

				return new SqlFunctionCallExpression(typeof(bool), SqlFunction.Like, operand1, operand2);
			}
			else if (methodCallExpression.Method.DeclaringType == typeof(string))
			{
				switch (methodCallExpression.Method.Name)
				{
					case "Contains":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count == 1)
						{
							var operand1 = Visit(methodCallExpression.Arguments[0]);

							return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.ContainsString, operand0, operand1);
						}

						break;
					}
					case "StartsWith":
					{
						var operand0 = Visit(methodCallExpression.Object);
							
						if (methodCallExpression.Arguments.Count == 1)
						{
							var operand1 = Visit(methodCallExpression.Arguments[0]);

							return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.StartsWith, operand0, operand1);
						}

						break;
					}
					case "EndsWith":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count == 1)
						{
							var operand1 = Visit(methodCallExpression.Arguments[0]);

							return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.EndsWith, operand0, operand1);
						}

						break;
					}
					case "Substring":
					{
						var operand0 = Visit(methodCallExpression.Object);
						var operand1 = Visit(methodCallExpression.Arguments[0]);
						var operand2 = Visit(methodCallExpression.Arguments[1]);

						if (methodCallExpression.Arguments.Count > 3)
						{
							var operand3 = Visit(methodCallExpression.Arguments[1]);

							return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Substring, operand0, operand1,
								                                    operand2, operand3);
						}
						else
						{
							return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Substring, operand0, operand1,
								                                    operand2);
						}
					}
					case "Trim":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count == 1)
						{
							var newArrayExpression = methodCallExpression.Arguments[0] as NewArrayExpression;

							if (newArrayExpression == null || newArrayExpression.Expressions.Count > 0)
							{
								throw new NotSupportedException("String.Trim(char[])");
							}
						}

						return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Trim, operand0);
					}
					case "TrimStart":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count == 1)
						{
							var newArrayExpression = methodCallExpression.Arguments[0] as NewArrayExpression;
							var constantExpression = methodCallExpression.Arguments[0] as ConstantExpression;
							var constantPlaceholderExpression = methodCallExpression.Arguments[0] as SqlConstantPlaceholderExpression;

							if ((newArrayExpression == null || newArrayExpression.Expressions.Count > 0)
								&& constantExpression == null && constantPlaceholderExpression == null)
							{
								throw new NotSupportedException("String.TrimStart(char[])");
							}
						}

						return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.TrimLeft, operand0);
					}
					case "TrimEnd":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count == 1)
						{
							var newArrayExpression = methodCallExpression.Arguments[0] as NewArrayExpression;
							var constantExpression = methodCallExpression.Arguments[0] as ConstantExpression;
							var constantPlaceholderExpression = methodCallExpression.Arguments[0] as SqlConstantPlaceholderExpression;

							if ((newArrayExpression == null || newArrayExpression.Expressions.Count > 0)
								&& constantExpression == null && constantPlaceholderExpression == null)
							{
								throw new NotSupportedException("String.TrimEnd(char[])");
							}
						}

						return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.TrimRight, operand0);
					}
					case "ToUpper":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count != 0)
						{
							throw new NotSupportedException("String.Upper()");
						}

						return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Upper, operand0);
						}
					case "ToLower":
					{
						var operand0 = Visit(methodCallExpression.Object);

						if (methodCallExpression.Arguments.Count != 0)
						{
							throw new NotSupportedException("String.Lower()");
						}

						return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Lower, operand0);
					}
				}
			}
			else if (methodCallExpression.Method.ReturnType.IsDataAccessObjectType())
			{
				return CreateObjectReference(methodCallExpression);
			}

			return base.VisitMethodCall(methodCallExpression);
		}
        
		protected virtual Expression BindOrderBy(Type resultType, Expression source, LambdaExpression orderSelector, OrderType orderType)
		{
			var myThenBys = this.thenBys;
			
			this.thenBys = null;
			
			var orderings = new List<SqlOrderByExpression>();
			var projection = (SqlProjectionExpression)this.Visit(source);

			AddExpressionByParameter(orderSelector.Parameters[0], projection.Projector);
			orderings.Add(new SqlOrderByExpression(orderType, this.Visit(orderSelector.Body)));

			if (myThenBys != null)
			{
				for (var i = myThenBys.Count - 1; i >= 0; i--)
				{
					var thenBy = myThenBys[i];
					var lambda = (LambdaExpression)thenBy.Expression;

					AddExpressionByParameter(lambda.Parameters[0], projection.Projector);
					orderings.Add(new SqlOrderByExpression(thenBy.OrderType, this.Visit(lambda.Body)));
				}
			}

			var alias = this.GetNextAlias();
			var projectedColumns = ProjectColumns(projection.Projector, alias, projection.Select.Alias);
			
			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, projection.Select, null, orderings.AsReadOnly(), projection.Select.ForUpdate), projectedColumns.Projector, null);
		}

		protected virtual Expression BindThenBy(Expression source, LambdaExpression orderSelector, OrderType orderType)
		{
			if (this.thenBys == null)
			{
				this.thenBys = new List<SqlOrderByExpression>();
			}

			this.thenBys.Add(new SqlOrderByExpression(orderType, orderSelector));

			return this.Visit(source);
		}

		private Expression BindWhere(Type resultType, Expression source, LambdaExpression predicate, bool forUpdate)
		{
			var projection = (SqlProjectionExpression)this.Visit(source);

			AddExpressionByParameter(predicate.Parameters[0], projection.Projector);

			var where = this.Visit(predicate.Body);

			var alias = this.GetNextAlias();

			var pc = ProjectColumns(projection.Projector, alias, GetExistingAlias(projection.Select));

			var retval = new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, where, null, forUpdate), pc.Projector, null);

			return retval;
		}

		private Expression BindDistinct(Type resultType, Expression source)
		{
			var projection = this.VisitSequence(source);
			var select = projection.Select;
			var alias = this.GetNextAlias();

			var projectedColumns = ProjectColumns(projection.Projector, alias, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, projection.Select, null, null, null, true, null, null, select.ForUpdate), projectedColumns.Projector, null);
		}

		private Expression currentGroupElement;

		private static SqlAggregateType GetAggregateType(string methodName)
		{
			switch (methodName)
			{
				case "Count":
					return SqlAggregateType.Count;
				case "Min":
					return SqlAggregateType.Min;
				case "Max":
					return SqlAggregateType.Max;
				case "Sum":
					return SqlAggregateType.Sum;
				case "Average":
					return SqlAggregateType.Average;
				default:
					throw new Exception(String.Concat("Unknown aggregate type: ", methodName));
			}
		}

		private static bool HasPredicateArg(SqlAggregateType aggregateType)
		{
			return aggregateType == SqlAggregateType.Count;
		}

		private SqlProjectionExpression VisitSequence(Expression source)
		{
			return ConvertToSequence(this.Visit(source));
		}

		private SqlProjectionExpression ConvertToSequence(Expression expression)
		{
			switch (expression.NodeType)
			{
				case (ExpressionType)SqlExpressionType.Projection:
					return (SqlProjectionExpression)expression;
				case ExpressionType.New:
					var newExpression = (NewExpression)expression;

					if (expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == typeof(Grouping<,>))
					{
						return (SqlProjectionExpression)newExpression.Arguments[1];
					}

					goto default;
				case ExpressionType.MemberAccess:
					var memberAccessExpression = (MemberExpression)expression;

					if (expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == TypeHelper.RelatedDataAccessObjectsType)
					{
						var typeDescriptor = this.DataAccessModel.GetTypeDescriptor(expression.Type.GetGenericArguments()[0]);
						var parentTypeDescriptor = this.DataAccessModel.GetTypeDescriptor(memberAccessExpression.Expression.Type);
						var source = Expression.Constant(null, this.DataAccessModel.AssemblyBuildInfo.GetDataAccessObjectsType(typeDescriptor.Type));
						var concreteType = this.DataAccessModel.GetConcreteTypeFromDefinitionType(typeDescriptor.Type);
						var parameter = Expression.Parameter(typeDescriptor.Type, "relatedObject");
						PropertyDescriptor relatedProperty = typeDescriptor.GetRelatedProperty(parentTypeDescriptor.Type);

						var relatedPropertyName = relatedProperty.PersistedName;

						var body = Expression.Equal
						(
							Expression.Property(parameter, relatedProperty.PropertyInfo),
							memberAccessExpression.Expression
						);

						var condition = Expression.Lambda(body, parameter);

						return (SqlProjectionExpression)BindWhere(expression.Type.GetGenericArguments()[0], source, condition, false);
					}
					else if (expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == TypeHelper.IQueryableType)
					{
						if (memberAccessExpression.Expression.NodeType == ExpressionType.Constant)
						{
							return null;
						}

						var elementType = TypeHelper.GetElementType(expression.Type);

						return GetTableProjection(elementType);
					}
					goto default;
				default:
					throw new Exception(string.Format("The expression of type '{0}' is not a sequence", expression.Type));
			}
		}

		private Expression BindAggregate(Expression source, MethodInfo method, LambdaExpression argument, bool isRoot)
		{
			var isDistinct = false;
			var returnType = method.ReturnType;
			var aggregateType = GetAggregateType(method.Name);
			var hasPredicateArg = HasPredicateArg(aggregateType);

			// Check for distinct
			var methodCallExpression = source as MethodCallExpression;
			
			if (methodCallExpression != null && !hasPredicateArg && argument == null)
			{
				if (methodCallExpression.Method.Name == "Distinct"
					&& methodCallExpression.Arguments.Count == 1
					&& (methodCallExpression.Method.DeclaringType == typeof(Queryable) || methodCallExpression.Method.DeclaringType == typeof(Enumerable)))
				{
					source = methodCallExpression.Arguments[0];

					isDistinct = true;
				}
			}

			var projection = this.VisitSequence(source);

			Expression argumentExpression = null;

			if (argument != null)
			{
				AddExpressionByParameter(argument.Parameters[0], projection.Projector);

				argumentExpression = this.Visit(argument.Body);
			}
			else if (!hasPredicateArg)
			{
				argumentExpression = projection.Projector;
			}

			var alias = this.GetNextAlias();

			var aggregateExpression = new SqlAggregateExpression(returnType, aggregateType, argumentExpression, isDistinct);
			var selectType = this.DataAccessModel.AssemblyBuildInfo.GetEnumerableType(returnType);

			var select = new SqlSelectExpression
			(
				selectType,
				alias,
				new [] { new SqlColumnDeclaration("", aggregateExpression) },
				projection.Select,
				null,
				null,
                projection.Select.ForUpdate
			);

			if (isRoot)
			{
				var parameterExpression = Expression.Parameter(selectType, "PARAM");

				var aggregator = Expression.Lambda(Expression.Call(typeof(Enumerable), "Single", new[] { returnType }, parameterExpression), parameterExpression);

				return new SqlProjectionExpression(select, new SqlColumnExpression(returnType, alias, ""), aggregator, false, SelectFirstType.None, projection.DefaultValueExpression, projection.IsDefaultIfEmpty);
			}

			var subquery = new SqlSubqueryExpression(returnType, select);

			// If we can find the corresponding group info then we can build a n
			// AggregateSubquery node that will enable us to optimize the aggregate
			// expression later using AggregateSubqueryRewriter

			GroupByInfo info;

			if (this.groupByMap.TryGetValue(projection, out info))
			{
				// Use the element expression from the group by info to rebind the
				// argument so the resulting expression is one that would be legal 
				// to add to the columns in the select expression that has the corresponding 
				// group-by clause.

				if (argument != null)
				{
					AddExpressionByParameter(argument.Parameters[0], info.Element);

					argumentExpression = this.Visit(argument.Body);
				}
				else if (!hasPredicateArg)
				{
					argumentExpression = info.Element;
				}

				aggregateExpression = new SqlAggregateExpression(returnType, aggregateType, argumentExpression, isDistinct);

				// Check for easy to optimize case.
				// If the projection that our aggregate is based on is really the 'group' argument from
				// the Query.GroupBy(xxx, (key, group) => yyy) method then whatever expression we return
				// here will automatically become part of the select expression that has the group-by
				// clause, so just return the simple aggregate expression.

				if (projection == this.currentGroupElement)
				{
					return aggregateExpression;
				}

				return new SqlAggregateSubqueryExpression(info.Alias, aggregateExpression, subquery);
			}

			return subquery;
		}

		private Expression BindSelect(Type resultType, Expression source, LambdaExpression selector, bool forUpdate)
		{
			Expression expression;
			var oldIsWithinClientSideCode = this.isWithinClientSideCode;
			var projection = (SqlProjectionExpression)this.Visit(source);

			AddExpressionByParameter(selector.Parameters[0], projection.Projector);

			this.isWithinClientSideCode = true;

			try
			{	
				expression = this.Visit(selector.Body);
			}
			finally
			{
				this.isWithinClientSideCode = oldIsWithinClientSideCode;
			}

			var alias = this.GetNextAlias();
			var pc = ProjectColumns(expression, alias, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, null, null, forUpdate || projection.Select.ForUpdate), pc.Projector, null);
		}

		private Expression BindDelete(Expression source, LambdaExpression selector)
		{
			var localExtraCondition = this.extraCondition;

			this.extraCondition = null;

			var projection = this.GetTableProjection(((ConstantExpression)source).Type);

			AddExpressionByParameter(selector.Parameters[0], projection.Projector);
			
			if (localExtraCondition != null)
			{
				AddExpressionByParameter(localExtraCondition.Parameters[0], projection.Projector);
			}

			var expression = this.Visit(selector.Body);
			
			var tableExpression = ((SqlTableExpression)projection.Select.From);
            
			if (localExtraCondition != null)
			{
				expression = Expression.AndAlso(selector.Body, localExtraCondition.Body);
				expression = Visit(expression);
			}

			return new SqlDeleteExpression(tableExpression.Name, projection.Select.Alias, expression);
		}

		private static string GetExistingAlias(Expression source)
		{
			switch ((SqlExpressionType)source.NodeType)
			{
				case SqlExpressionType.Select:
					return ((SqlSelectExpression)source).Alias;
				case SqlExpressionType.Table:
					return ((SqlTableExpression)source).Alias;
				default:
					throw new InvalidOperationException(String.Concat("Invalid source node type: ", source.NodeType));
			}
		}

		private class MemberInitEqualityComparer
			: IEqualityComparer<MemberInitExpression>
		{
			public static readonly MemberInitEqualityComparer Default = new MemberInitEqualityComparer();

			public bool Equals(MemberInitExpression x, MemberInitExpression y)
			{
				return x.Type == y.Type && x.Bindings.Count == y.Bindings.Count;
			}

			public int GetHashCode(MemberInitExpression obj)
			{
				return obj.Type.GetHashCode() ^ obj.Bindings.Count;
			}
		}

		private SqlProjectionExpression GetTableProjection(Type type)
		{
			Type elementType;
			TypeDescriptor typeDescriptor;

			if (type.IsDataAccessObjectType())
			{
				typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(type);

				elementType = typeDescriptor.Type;
			}
			else
			{
				typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(TypeHelper.GetElementType(type));

				elementType = typeDescriptor.Type;
			}

			var tableAlias = this.GetNextAlias();
			var selectAlias = this.GetNextAlias();

			var rootBindings = new List<MemberBinding>();
			var tableColumns = new List<SqlColumnDeclaration>();

			var columnInfos = QueryBinder.GetColumnInfos
			(
				this.typeDescriptorProvider,
				typeDescriptor.PersistedAndRelatedObjectProperties,
				(c, d) => d == 0 || c.IsPrimaryKey,
				(c, d) => d == 0 || c.IsPrimaryKey
			);

			var groupedColumnInfos = columnInfos
				.GroupBy(c => c.VisitedProperties, ArrayEqualityComparer<PropertyDescriptor>.Default)
				.OrderBy(c => c.Key.Length);

			var bindingsForKey = groupedColumnInfos
				.ToDictionary(c => c.Key, c => c.Key.Length == 0 ? rootBindings : new List<MemberBinding>(), ArrayEqualityComparer<PropertyDescriptor>.Default);

			var parentBindingsForKey = bindingsForKey
				.Where(c => c.Key.Length > 0)
				.ToDictionary(c => c.Key, c => bindingsForKey[c.Key.Take(c.Key.Length - 1).ToArray()], ArrayEqualityComparer<PropertyDescriptor>.Default);

			var rootPrimaryKeyProperties = new HashSet<string>(typeDescriptor.PrimaryKeyProperties.Select(c => c.PropertyName));

			foreach (var groupedColumnInfo in groupedColumnInfos)
			{
				var currentBindings = bindingsForKey[groupedColumnInfo.Key];

				foreach (var value in groupedColumnInfo)
				{
					var propertyInfo = value.DefinitionProperty.PropertyInfo;
					var columnExpression = new SqlColumnExpression(propertyInfo.PropertyType, selectAlias, value.ColumnName);

					currentBindings.Add(Expression.Bind(propertyInfo, columnExpression));
					tableColumns.Add(new SqlColumnDeclaration(value.ColumnName, new SqlColumnExpression(propertyInfo.PropertyType, tableAlias, value.ColumnName)));
				}
			}

			foreach (var value in groupedColumnInfos.Where(c => c.Key.Length > 0).OrderByDescending(c => c.Key.Length))
			{
				var property = value.Key[value.Key.Length - 1];
				var objectReferenceType = property.PropertyType;
				var parentBindings = parentBindingsForKey[value.Key];

				var objectReference = new SqlObjectReference(objectReferenceType, bindingsForKey[value.Key]);

				if (objectReference.Bindings.Count == 0)
				{
					throw new InvalidOperationException(string.Format("Missing ObjectReference bindings: {0}.{1}", property.PropertyInfo.ReflectedType , property.PropertyName));
				}

				parentBindings.Add(Expression.Bind(property.PropertyInfo, objectReference));
			}

			var rootObjectReference = new SqlObjectReference(typeDescriptor.Type, rootBindings.Where(c => rootPrimaryKeyProperties.Contains(c.Member.Name)));

			if (rootObjectReference.Bindings.Count == 0)
			{
				throw new InvalidOperationException(string.Format("Missing ObjectReference bindings: {0}", type.Name));
			}

			var projectorExpression = Expression.MemberInit(Expression.New(elementType), rootBindings);
			this.objectReferenceByMemberInit[projectorExpression] = rootObjectReference;

			var resultType = this.DataAccessModel.AssemblyBuildInfo.GetEnumerableType(elementType);
			var projection = new SqlProjectionExpression(new SqlSelectExpression(resultType, selectAlias, tableColumns, new SqlTableExpression(resultType, tableAlias, typeDescriptor.PersistedName), null, null, false), projectorExpression, null);

			if ((conditionType == elementType || (conditionType != null && conditionType.IsAssignableFrom(elementType))) && extraCondition != null)
			{
				AddExpressionByParameter(extraCondition.Parameters[0], projection.Projector);

				var where = this.Visit(this.extraCondition.Body);
				var alias = this.GetNextAlias();
				var pc = ProjectColumns(projection.Projector, alias, GetExistingAlias(projection.Select));

				return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, where, null, false), pc.Projector, null);
			}

			return projection;
		}

		public static bool IsTable(Type type)
		{
			if (type.IsGenericType)
			{
				var genericType = type.GetGenericTypeDefinition();
				var elementType = type.GetGenericArguments()[0];

				var retval = 
					genericType == TypeHelper.DataAccessObjectsType 
					|| genericType == TypeHelper.RelatedDataAccessObjectsType
					|| genericType == typeof(IQueryable<>) && elementType.IsDataAccessObjectType()
					|| genericType == typeof(SqlQueryable<>) && elementType.IsDataAccessObjectType()
					|| genericType == typeof(ReusableQueryable<>) && elementType.IsDataAccessObjectType()
					|| genericType.GetInterfaces().Any(c => c == typeof(IQueryable)) && elementType.IsDataAccessObjectType();

				return retval;
			}

			return false;
		}

		protected override Expression VisitConstant(ConstantExpression constantExpression)
		{
			var type = constantExpression.Type;

			if (constantExpression.Value != null)
			{
				type = constantExpression.Value.GetType();
			}

			if (IsTable(type))
			{
				var retval = GetTableProjection(type);

				return retval;
			}
			else if (type.IsDataAccessObjectType() && !this.isWithinClientSideCode)
			{
				return CreateObjectReference(constantExpression);
			}

			return constantExpression;
		}
        
		protected override Expression VisitParameter(ParameterExpression p)
		{
			Expression e;

			if (this.expressionsByParameter.TryGetValue(p, out e))
			{
				return e;
			}

			return p;
		}

		protected SqlObjectReference CreateObjectReference(Expression expression)
		{
			var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(this.DataAccessModel.GetDefinitionTypeFromConcreteType(expression.Type));

			var columnInfos = QueryBinder.GetColumnInfos
			(
				this.typeDescriptorProvider,
				typeDescriptor.PersistedAndRelatedObjectProperties,
				(c, d) => c.IsPrimaryKey,
				(c, d) => c.IsPrimaryKey
			);

			var groupedColumnInfos = columnInfos
				.GroupBy(c => c.VisitedProperties, ArrayEqualityComparer<PropertyDescriptor>.Default)
				.OrderBy(c => c.Key.Length);

			var expressionForKey = new Dictionary<PropertyDescriptor[], Expression>(ArrayEqualityComparer<PropertyDescriptor>.Default);
			
			var bindings = new List<MemberBinding>();

			foreach (var groupedColumnInfo in groupedColumnInfos)
			{
				Expression parentExpression;
				
				if (groupedColumnInfo.Key.Length == 0)
				{
					parentExpression = expression;
				}
				else
				{
					parentExpression = Expression.Property(expressionForKey[groupedColumnInfo.Key.Take(groupedColumnInfo.Key.Length - 1).ToArray()], groupedColumnInfo.Key[groupedColumnInfo.Key.Length - 1].PropertyInfo);
				}

				expressionForKey[groupedColumnInfo.Key] = parentExpression;

				foreach (var value in groupedColumnInfo)
				{
					var propertyInfo = value.DefinitionProperty.PropertyInfo;
					var propertyAccess = Expression.Property(parentExpression, value.DefinitionProperty.PropertyName);

					bindings.Add(Expression.Bind(propertyInfo, propertyAccess));
				}
			}

			if (bindings.Count == 0)
			{
				throw new Exception(string.Format("Missing bindings for: {0}", expression));
			}

			return new SqlObjectReference(expression.Type, bindings);
		}

		protected override Expression VisitMemberAccess(MemberExpression memberExpression)
		{
			var source = this.Visit(memberExpression.Expression);

			if (source == null)
			{
				return MakeMemberAccess(null, memberExpression.Member);
			}

			switch (source.NodeType)
			{
				case ExpressionType.MemberInit:
					var min = (MemberInitExpression)source;

					if (min.Bindings != null)
					{
						for (int i = 0, n = min.Bindings.Count; i < n; i++)
						{
							var assign = min.Bindings[i] as MemberAssignment;

							if (assign != null && MembersMatch(assign.Member, memberExpression.Member))
							{
								return assign.Expression;
							}
						}
					}

					break;
				case ExpressionType.New:
					// Source is a anonymous type from a join
					var newExpression = (NewExpression)source;

					if (newExpression.Members != null)
					{
						for (int i = 0, n = newExpression.Members.Count; i < n; i++)
						{
							if (MembersMatch(newExpression.Members[i], memberExpression.Member))
							{
								return newExpression.Arguments[i];
							}
						}
					}
				break;
				case ExpressionType.Constant:

					if (memberExpression.Type.IsDataAccessObjectType())
					{
						return CreateObjectReference(memberExpression);
					}
					else if (IsTable(memberExpression.Type))
					{
						return GetTableProjection(memberExpression.Type);
					}
					else
					{
						return memberExpression;
					}
			}
            
			if (source == memberExpression.Expression)
			{
				return memberExpression;
			}

			return MakeMemberAccess(source, memberExpression.Member);
		}

		private static bool MemberInfosMostlyMatch(MemberInfo a, MemberInfo b)
		{
			if (a == b)
			{
				return true;
			}

			if (a.GetType() == b.GetType())
			{
				if (a.Name == b.Name
					&& ((a.DeclaringType == b.DeclaringType) || a.DeclaringType.IsAssignableFrom(b.DeclaringType) || b.DeclaringType.IsAssignableFrom(a.DeclaringType)))
				{
					return true;
				}
			}

			return false;
		}

		private static bool MembersMatch(MemberInfo a, MemberInfo b)
		{
			if (a == b)
			{
				return true;
			}

			if (MemberInfosMostlyMatch(a, b))
			{
				return true;
			}

			if (a is MethodInfo && b is PropertyInfo)
			{
				return MemberInfosMostlyMatch(a, ((PropertyInfo)b).GetGetMethod());
			}
			else if (a is PropertyInfo && b is MethodInfo)
			{
				return MemberInfosMostlyMatch(((PropertyInfo)a).GetGetMethod(), b);
			}

			return false;
		}

		protected override Expression VisitUnary(UnaryExpression unaryExpression)
		{
			if (unaryExpression.NodeType == ExpressionType.Not)
			{
				if (unaryExpression.Operand.NodeType == ExpressionType.Call)
				{
					if (((MethodCallExpression)unaryExpression.Operand).Method == typeof(ShaolinqStringExtensions).GetMethod("IsLike",BindingFlags.Static | BindingFlags.Public))
					{
						var methodCallExpression = (MethodCallExpression)unaryExpression.Operand;

						var operand1 = Visit(methodCallExpression.Arguments[0]);
						var operand2 = Visit(methodCallExpression.Arguments[1]);
                        
						return new SqlFunctionCallExpression(typeof(bool), SqlFunction.NotLike, operand1, operand2);
					}
				}
			}

			return base.VisitUnary(unaryExpression);
		}

		private Expression MakeMemberAccess(Expression source, MemberInfo memberInfo)
		{
			var fieldInfo = memberInfo as FieldInfo;

			if (typeof(ICollection<>).IsAssignableFromIgnoreGenericParameters(memberInfo.DeclaringType) && !this.isWithinClientSideCode)
			{
				if (memberInfo.Name == "Count")
				{
					return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.CollectionCount, source);
				}
			}
			else if (memberInfo.DeclaringType == typeof(DateTime) && !this.isWithinClientSideCode)
			{
				switch (memberInfo.Name)
				{
					case "Week":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Week, source);
					case "Month":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Month, source);
					case "Year":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Year, source);
					case "Hour":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Hour, source);
					case "Minute":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Minute, source);
					case "Second":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Second, source);
					case "DayOfWeek":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.DayOfWeek, source);
					case "Day":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.DayOfMonth, source);
					case "DayOfYear":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.DayOfYear, source);
					case "Date":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Date, source);
					default:
						throw new NotSupportedException("Member access on DateTime: " + memberInfo);
				}
			}
			else if (memberInfo.DeclaringType == typeof(ServerDateTime))
			{
				switch (memberInfo.Name)
				{
					case "Now":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.ServerDateTime);
				}
			}
			else if (typeof(IGrouping<,>).IsAssignableFromIgnoreGenericParameters(memberInfo.DeclaringType) && source is NewExpression && memberInfo.Name == "Key")
			{
				var newExpression = source as NewExpression;

				var arg = newExpression.Arguments[0];

				return arg;
			}
			else if (source != null && source.NodeType == (ExpressionType)SqlExpressionType.ObjectReference)
			{
				var objectOperandExpression = (SqlObjectReference)source;
				var binding = objectOperandExpression.Bindings.OfType<MemberAssignment>().FirstOrDefault(c => c.Member.Name == memberInfo.Name);

				if (binding != null)
				{
					return binding.Expression;
				}
			}

			if (fieldInfo != null)
			{
				return Expression.Field(source, fieldInfo);
			}

			var propertyInfo = memberInfo as PropertyInfo;

			if (propertyInfo != null)
			{
				// TODO: Throw an unsupported exception if not binding for SQL ToString implementation

				return Expression.Property(source, propertyInfo);
			}

			throw new NotSupportedException("MemberInfo: " + memberInfo);
		}

		internal static string GetKey(Dictionary<string, Expression> dictionary, Expression expression)
		{
			foreach (var keyValue in dictionary)
			{
				if (keyValue.Value == expression)
				{
					return keyValue.Key;
				}
			}

			throw new InvalidOperationException();
		}

		protected override Expression Visit(Expression expression)
		{
			if (expression == null)
			{
				return null;
			}

			List<IncludedPropertyInfo> includedPropertyInfos;

			if (this.joinExpanderResults.IncludedPropertyInfos.TryGetValue(expression, out includedPropertyInfos))
			{
				var newExpression = PrivateVisit(expression);
				var replacements = new Dictionary<Expression, Expression>();

				foreach (var includedProperty in includedPropertyInfos.OrderBy(c => c.SuffixPropertyPath.Length))
				{
					Expression current = newExpression;

					foreach (var propertyInfo in includedProperty.SuffixPropertyPath)
					{
						Expression replacementCurrent;

						if (replacements.TryGetValue(current, out replacementCurrent))
						{
							current = replacementCurrent;
						}

						MemberInitExpression currentMemberInit;

						if (current.NodeType == ExpressionType.Conditional)
						{
							currentMemberInit = (MemberInitExpression)((ConditionalExpression)current).IfFalse;
						}
						else
						{
							currentMemberInit = (MemberInitExpression)current;
						}

						current = ((MemberAssignment)(currentMemberInit).Bindings.First(c => c.Member.Name == propertyInfo.Name)).Expression;
					}

					var objectReference = current as SqlObjectReference;

					Expression isNullExpression = null;

					if (objectReference != null)
					{
						foreach (var binding in objectReference.Bindings.OfType<MemberAssignment>())
						{
							Expression equalExpression;
							var columnExpression = binding.Expression as SqlColumnExpression;

							if (columnExpression != null)
							{
								var nullableType = columnExpression.Type.MakeNullable();

								if (columnExpression.Type == nullableType)
								{
									equalExpression = Expression.Equal(columnExpression, Expression.Constant(null, columnExpression.Type));
								}
								else
								{
									equalExpression = Expression.Equal(Expression.Convert(columnExpression, nullableType), Expression.Constant(nullableType.GetDefaultValue(), nullableType));
								}
							}
							else if (binding.Expression is SqlObjectReference)
							{
								equalExpression = Expression.Equal(binding.Expression, Expression.Constant(null));
							}
							else
							{
								isNullExpression = null;

								break;
							}

							if (isNullExpression == null)
							{
								isNullExpression = equalExpression;
							}
							else
							{
								isNullExpression = Expression.Or(isNullExpression, equalExpression);
							}
						}
					}

					var originalReplacementExpression = this.joinExpanderResults.GetReplacementExpression(this.selectorPredicateStack.Peek(), includedProperty.PropertyPath);

					var replacement = this.Visit(originalReplacementExpression);

					if (isNullExpression != null)
					{
						var condition = Expression.Condition(isNullExpression, Expression.Constant(null, current.Type), replacement);

						newExpression = ExpressionReplacer.Replace(newExpression, current, condition);
					}
					else
					{
						newExpression = ExpressionReplacer.Replace(newExpression, current, replacement);
					}
				}

				return newExpression;
			}

			return PrivateVisit(expression);
		}

		private Expression PrivateVisit(Expression expression)
		{
			if (expression == null)
			{
				return null;
			}

			switch (expression.NodeType)
			{
			case (ExpressionType)SqlExpressionType.ConstantPlaceholder:
				var result = Visit(((SqlConstantPlaceholderExpression)expression).ConstantExpression);

				if (!(result is ConstantExpression))
				{
					return result;
				}

				return expression;
			case (ExpressionType)SqlExpressionType.Column:
				return expression;
			}

			return base.Visit(expression);
		}
	}
}
