﻿// Copyright (c) 2007-2016 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Collections.Generic;
using Platform.Xml.Serialization;

namespace Shaolinq.TypeBuilding
{
	public class CachingDataAccessModelAssemblyProvider
		: DataAccessAssemblyProvider
	{
		public static readonly CachingDataAccessModelAssemblyProvider Default = new CachingDataAccessModelAssemblyProvider(new DataAccessModelAssemblyBuilder());

		private struct AssemblyKey
		{
			private readonly string configurationXml;
			private readonly string configurationHash;
			private readonly Type dataAccessModelType;
			private readonly DataAccessModelConfiguration configuration;
			
			public AssemblyKey(Type dataAccessModelType, DataAccessModelConfiguration configuration)
			{
				this.configuration = configuration;
				this.dataAccessModelType = dataAccessModelType;
				this.configurationHash = configuration.GetSha1();
				this.configurationXml = XmlSerializer<DataAccessModelConfiguration>.New().SerializeToString(configuration);
			}

			public override int GetHashCode()
			{
				return this.configurationHash.GetHashCode() ^ this.dataAccessModelType.GetHashCode();
			}

			public override bool Equals(object obj)
			{
				var other = obj as AssemblyKey?;

				if (other == null)
				{
					return false;
				}

				var serializer = XmlSerializer<DataAccessModelConfiguration>.New();
				
				return other.Value.configurationHash == this.configurationHash
					   && other.Value.dataAccessModelType == this.dataAccessModelType
					   && (this.configurationXml == serializer.SerializeToString(other.Value.configuration));
			}
		}

		private readonly DataAccessAssemblyProvider provider;
		private readonly Dictionary<AssemblyKey, RuntimeDataAccessModelInfo> assemblyBuildInfosByKey = new Dictionary<AssemblyKey, RuntimeDataAccessModelInfo>();

		public CachingDataAccessModelAssemblyProvider(DataAccessAssemblyProvider provider)
		{
			this.provider = provider;
		}

		public override RuntimeDataAccessModelInfo GetDataAccessModelAssembly(Type dataAccessModelType, DataAccessModelConfiguration configuration)
		{
			var key = new AssemblyKey(dataAccessModelType, configuration);

			RuntimeDataAccessModelInfo runtimeDataAccessModelInfo;

			lock (this.assemblyBuildInfosByKey)
			{
				if (!this.assemblyBuildInfosByKey.TryGetValue(key, out runtimeDataAccessModelInfo))
				{
					runtimeDataAccessModelInfo = this.provider.GetDataAccessModelAssembly(dataAccessModelType, configuration);

					this.assemblyBuildInfosByKey[key] = runtimeDataAccessModelInfo;
				}
			}

			return runtimeDataAccessModelInfo;
		}
	}
}