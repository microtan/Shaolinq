﻿// Copyright (c) 2007-2016 Thong Nguyen (tumtumtum@gmail.com)using System;

using System;
using System.Collections.Generic;
using System.Data;

namespace Shaolinq.Persistence
{
	public class MarsDataReader
		: DataReaderWrapper
	{
		private bool closed;
		public string[] names;
		private int fieldCount;
		private Type[] fieldTypes;
		private object[] currentRow;
		private int recordsAffected;
		private Queue<object[]> rows;
		private string[] dataTypeNames;
		private readonly MarsDbCommand command;
		private Dictionary<string, int> ordinalByFieldName;
		
		public MarsDataReader(MarsDbCommand command, IDataReader inner)
			: base(inner)
		{
			command.context.currentReader = this;

			this.command = command;
		}

		public void BufferAll()
		{
			if (this.IsClosed || this.closed)
			{
				return;
			}

			this.rows = new Queue<object[]>();

			try
			{
				this.fieldCount = base.FieldCount;
				this.recordsAffected = base.RecordsAffected;
				this.ordinalByFieldName = new Dictionary<string, int>(this.fieldCount);
				this.dataTypeNames = new string[this.fieldCount];
				this.fieldTypes = new Type[this.fieldCount];
				this.names = new string[this.fieldCount];

				for (var i = 0; i < base.FieldCount; i++)
				{
					this.ordinalByFieldName[base.GetName(i)] = i;
					this.dataTypeNames[i] = base.GetDataTypeName(i);
					this.fieldTypes[i] = base.GetFieldType(i);
					this.names[i] = base.GetName(i);
				}

				while (base.Read())
				{
					var rowData = new object[base.FieldCount];

					base.GetValues(rowData);
					this.rows.Enqueue(rowData);
				}
			}
			finally
			{
				this.Dispose();
			}
		}

		public override void Close()
		{
			if (this.rows == null)
			{
				base.Close();
			}

			this.closed = true;
			this.rows = null;
		}

		public override bool NextResult()
		{
			if (this.rows == null)
			{
				return base.NextResult();
			}

			throw new NotImplementedException();
		}

		public override bool Read()
		{
			if (this.rows == null)
			{
				return base.Read();
			}

			if (this.rows.Count == 0)
			{
				this.currentRow = null;

				return false;
			}

			this.currentRow = this.rows.Dequeue();

			return true;
		}

		public override int Depth
		{
			get
			{
				if (this.rows == null)
				{
					return base.Depth;
				}

				throw new NotImplementedException();
			}
		}

		public override void Dispose()
		{
			if (this.command.context.currentReader == this)
			{
				this.command.context.currentReader = null;

				base.Dispose();
			}
		}

		public override string GetName(int i)
		{
			if (this.rows == null)
			{
				return base.GetName(i);
			}

			return this.names[i];
		}

		public override string GetDataTypeName(int i)
		{
			if (this.rows == null)
			{
				return base.GetDataTypeName(i);
			}

			return this.dataTypeNames[i];
		}

		public override Type GetFieldType(int i)
		{
			if (this.rows == null)
			{
				return base.GetFieldType(i);
			}

			return this.fieldTypes[i];
		}

		public override object GetValue(int i)
		{
			if (this.rows == null)
			{
				return base.GetValue(i);
			}

			return this.currentRow[i];
		}

		public override int GetValues(object[] values)
		{
			if (this.rows == null)
			{
				return base.GetValues(values);
			}

			var x = Math.Min(this.currentRow.Length, values.Length);

			Array.Copy(this.currentRow, values, x);

			return x;
		}

		public override int GetOrdinal(string name)
		{
			if (this.rows == null)
			{
				return base.GetOrdinal(name);
			}

			return this.ordinalByFieldName[name];
		}

		public override bool GetBoolean(int i)
		{
			if (this.rows == null)
			{
				return base.GetBoolean(i);
			}

			return Convert.ToBoolean(this.currentRow[i]);
		}

		public override byte GetByte(int i)
		{
			if (this.rows == null)
			{
				return base.GetByte(i);
			}

			return Convert.ToByte(this.currentRow[i]);
		}

		public override long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length)
		{
			if (this.rows == null)
			{
				return base.GetBytes(i, fieldOffset, buffer, bufferoffset, length);
			}

			var bytes = (byte[])this.currentRow[i];

			var x = Math.Min(length, bytes.Length - fieldOffset);

			if (length < 0 || length > x)
			{
				throw new ArgumentOutOfRangeException(nameof(length));
			}

			Array.Copy(bytes, fieldOffset, buffer, bufferoffset, length);

			return length;
		}

		public override char GetChar(int i)
		{
			if (this.rows == null)
			{
				return base.GetChar(i);
			}

			return Convert.ToChar(this.currentRow[i]);
		}

		public override long GetChars(int i, long fieldOffset, char[] buffer, int bufferoffset, int length)
		{
			if (this.rows == null)
			{
				return base.GetChars(i, fieldOffset, buffer, bufferoffset, length);
			}

			var bytes = (byte[])this.currentRow[i];

			var x = Math.Min(length, bytes.Length - fieldOffset);

			if (length < 0 || length > x)
			{
				throw new ArgumentOutOfRangeException(nameof(length));
			}

			Array.Copy(bytes, fieldOffset, buffer, bufferoffset, length);

			return length;
		}

		public override Guid GetGuid(int i)
		{
			if (this.rows == null)
			{
				return base.GetGuid(i);
			}

			return (Guid)this.currentRow[i];
		}

		public override short GetInt16(int i)
		{
			if (this.rows == null)
			{
				return base.GetInt16(i);
			}

			return Convert.ToInt16(this.currentRow[i]);
		}

		public override int GetInt32(int i)
		{
			if (this.rows == null)
			{
				return base.GetInt32(i);
			}

			return Convert.ToInt32(this.currentRow[i]);
		}

		public override long GetInt64(int i)
		{
			if (this.rows == null)
			{
				return base.GetInt64(i);
			}

			return Convert.ToInt64(this.currentRow[i]);
		}

		public override float GetFloat(int i)
		{
			if (this.rows == null)
			{
				return base.GetFloat(i);
			}

			return Convert.ToSingle(this.currentRow[i]);
		}

		public override double GetDouble(int i)
		{
			if (this.rows == null)
			{
				return base.GetDouble(i);
			}

			return Convert.ToDouble(this.currentRow[i]);
		}

		public override string GetString(int i)
		{
			if (this.rows == null)
			{
				return base.GetString(i);
			}

			return Convert.ToString(this.currentRow[i]);
		}

		public override decimal GetDecimal(int i)
		{
			if (this.rows == null)
			{
				return base.GetDecimal(i);
			}

			return Convert.ToDecimal(this.currentRow[i]);
		}

		public override DateTime GetDateTime(int i)
		{
			if (this.rows == null)
			{
				return base.GetDateTime(i);
			}

			return Convert.ToDateTime(this.currentRow[i]);
		}

		public override IDataReader GetData(int i)
		{
			if (this.rows == null)
			{
				return base.GetData(i);
			}

			throw new NotSupportedException($"{nameof(MarsDataReader)}.{nameof(GetData)}");
		}

		public override bool IsDBNull(int i)
		{
			if (this.rows == null)
			{
				return base.IsDBNull(i);
			}

			return this.currentRow[i] == DBNull.Value;
		}

		public override int FieldCount => this.rows == null ? base.FieldCount : this.fieldCount;
		public override object this[int i] => this.rows == null ? base[i] : this.currentRow[i];
		public override object this[string name] => this.rows == null ? base[name] : this.currentRow[this.ordinalByFieldName[name]];
		public override bool IsClosed => this.rows == null ? base.IsClosed : this.closed;
		public override int RecordsAffected => this.rows == null ? base.RecordsAffected : this.recordsAffected;
	}
}
