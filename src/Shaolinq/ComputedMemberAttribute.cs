﻿// Copyright (c) 2007-2015 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Linq.Expressions;
using System.Reflection;
using Shaolinq.Persistence.Computed;

namespace Shaolinq
{
	[AttributeUsage(AttributeTargets.Property)]
	public class ComputedMemberAttribute
		: Attribute
	{
		public string Expression { get; set; }
		
		public ComputedMemberAttribute(string expression)
		{
			this.Expression = expression;
		}

		public LambdaExpression GetLambdaExpression(PropertyInfo propertyInfo)
		{
			return ComputedExpressionParser.Parse(this.Expression, propertyInfo);
		}
	}
}