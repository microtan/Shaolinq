﻿// Copyright (c) 2007-2016 Thong Nguyen (tumtumtum@gmail.com)

using Shaolinq.Tests.OtherDataAccessObjects;

namespace Shaolinq.Tests.TestModel
{
	[DataAccessModel]
	public abstract class TestDataAccessModel
		: DataAccessModel
	{
		[DataAccessObjects]
		public abstract DataAccessObjects<Bird> Birds { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Apple> Apples { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Address> Address { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Cat> Cats { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Dog> Dogs { get; }
		
		[DataAccessObjects]
		public abstract DataAccessObjects<Club> Club { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Paper> Papers { get; }
		
		[DataAccessObjects]
		public abstract DataAccessObjects<School> Schools { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Product> Products { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Student> Students { get; }
		
		[DataAccessObjects]
		public abstract DataAccessObjects<Lecturer> Lecturers { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<Fraternity> Fraternities { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithUniqueConstraint> ObjectWithUniqueConstraints { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<DefaultIfEmptyTestObject> DefaultIfEmptyTestObjects { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithCompositePrimaryKey> ObjectWithCompositePrimaryKeys { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithGuidAutoIncrementPrimaryKey> ObjectWithGuidAutoIncrementPrimaryKeys { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithGuidNonAutoIncrementPrimaryKey> ObjectWithGuidNonAutoIncrementPrimaryKeys { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithLongAutoIncrementPrimaryKey> ObjectWithLongAutoIncrementPrimaryKeys { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithLongNonAutoIncrementPrimaryKey> ObjectWithLongNonAutoIncrementPrimaryKeys { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithDaoPrimaryKey> ObjectWithDaoPrimaryKeys { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<ObjectWithManyTypes> ObjectWithManyTypes { get; }

		[DataAccessObjects]
		public abstract DataAccessObjects<NonPrimaryAutoIncrement> NonPrimaryAutoIncrementObjectWithManyTypes { get; }
	}
}
