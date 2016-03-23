﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.Serialization;
using static System.FormattableString;

namespace MySql.Data.MySqlClient
{
	public sealed class MySqlDataReader : DbDataReader
	{
		public override bool NextResult()
		{
			return NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();
		}

		public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
		{
			VerifyNotDisposed();

			if (m_state == State.ReadingRows)
				throw new NotImplementedException("TODO: Read until EOF");
			if (m_state == State.NoMoreData)
				return false;
			if (m_state != State.HasMoreData)
				throw new InvalidOperationException(Invariant($"Invalid state: {m_state}"));

			Reset();
			await ReadResultSetHeader(cancellationToken).ConfigureAwait(false);
			return true;
		}

		public override bool Read()
		{
			VerifyNotDisposed();
			return ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
		}

		public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
		{
			VerifyNotDisposed();

			// if we've already read past the end of this resultset, Read returns false
			if (m_state == State.HasMoreData || m_state == State.NoMoreData)
				return false;

			if (m_state != State.AlreadyReadFirstRow)
			{
				var payload = await m_session.ReceiveReplyAsync(cancellationToken).ConfigureAwait(false);

				var reader = new ByteArrayReader(payload.ArraySegment);
				var headerByte = reader.ReadByte();

				if (headerByte == 0xFE)
				{
					int warningCount = reader.ReadUInt16();
					var flags = (ServerStatus) reader.ReadUInt16();

					m_state = flags.HasFlag(ServerStatus.MoreResultsExist) ? State.HasMoreData : State.NoMoreData;
					return false;
				}

				reader.Offset--;
				for (var column = 0; column < m_dataOffsets.Length; column++)
				{
					var length = (int) reader.ReadLengthEncodedInteger();
					m_dataLengths[column] = length;
					m_dataOffsets[column] = length == 0xFB ? -1 : reader.Offset;
					reader.Offset += length == 0xFB ? 0 : length;
				}

				m_currentRow = payload.ArraySegment.Array;
			}

			m_state = State.ReadingRows;
			return true;
		}

		public override bool IsClosed => m_command == null;

		public override int RecordsAffected => m_recordsAffected;

		public override bool GetBoolean(int ordinal)
		{
			return (bool) GetValue(ordinal);
		}

		public override byte GetByte(int ordinal)
		{
			return (byte) GetValue(ordinal);
		}

		public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
		{
			throw new NotImplementedException();
		}

		public override char GetChar(int ordinal)
		{
			return (char) GetValue(ordinal);
		}

		public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
		{
			throw new NotImplementedException();
		}

		public override Guid GetGuid(int ordinal)
		{
			return (Guid) GetValue(ordinal);
		}

		public override short GetInt16(int ordinal)
		{
			return (short) GetValue(ordinal);
		}

		public override int GetInt32(int ordinal)
		{
			object value = GetValue(ordinal);
			if (value is short)
				return (short) value;
			else if (value is long)
				return checked((int) (long) value);
			return (int) value;
		}

		public override long GetInt64(int ordinal)
		{
			object value = GetValue(ordinal);
			if (value is short)
				return (short) value;
			if (value is int)
				return (int) value;
			return (long) value;
		}

		public override DateTime GetDateTime(int ordinal)
		{
			return (DateTime) GetValue(ordinal);
		}

		public override string GetString(int ordinal)
		{
			return (string) GetValue(ordinal);
		}

		public override decimal GetDecimal(int ordinal)
		{
			throw new NotImplementedException();
		}

		public override double GetDouble(int ordinal)
		{
			object value = GetValue(ordinal);
			if (value is float)
				return (float) value;
			return (double) value;
		}

		public override float GetFloat(int ordinal)
		{
			return (float) GetValue(ordinal);
		}

		public override string GetName(int ordinal)
		{
			VerifyHasResult();
			if (ordinal < 0 || ordinal > m_columnDefinitions.Length)
				throw new ArgumentOutOfRangeException(nameof(ordinal), Invariant($"value must be between 0 and {m_columnDefinitions.Length - 1}"));
			return m_columnDefinitions[ordinal].Name;
		}

		public override int GetValues(object[] values)
		{
			VerifyRead();
			int count = Math.Min(values.Length, m_columnDefinitions.Length);
			for (int i = 0; i < count; i++)
				values[i] = GetValue(i);
			return count;
		}

		public override bool IsDBNull(int ordinal)
		{
			VerifyRead();
			// TODO: Correct exception for invalid ordinal?
			return m_dataOffsets[ordinal] == -1;
		}

		public override int FieldCount
		{
			get
			{
				VerifyNotDisposed();
				return m_columnDefinitions.Length;
			}
		}

		public override object this[int ordinal] => GetValue(ordinal);

		public override object this[string name] => GetValue(GetOrdinal(name));

		public override bool HasRows
		{
			get
			{
				VerifyNotDisposed();
				if (m_state == State.ReadResultSetHeader)
				{
					if (Read())
					{
						m_state = State.AlreadyReadFirstRow;
						return true;
					}
					return false;
				}
				return m_state == State.ReadingRows;
			}
		}

		public override int GetOrdinal(string name)
		{
			VerifyHasResult();

			for (int column = 0; column < m_columnDefinitions.Length; column++)
			{
				if (m_columnDefinitions[column].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
					return column;
			}

			// TODO: Correct exception
			throw new IndexOutOfRangeException(Invariant($"The column name '{name}' does not exist in the result set."));
		}

		public override string GetDataTypeName(int ordinal)
		{
			throw new NotImplementedException();
		}

		public override Type GetFieldType(int ordinal)
		{
			VerifyHasResult();
			if (ordinal < 0 || ordinal > m_columnDefinitions.Length)
				throw new ArgumentOutOfRangeException(nameof(ordinal), Invariant($"value must be between 0 and {m_columnDefinitions.Length}."));

			switch (m_columnDefinitions[ordinal].ColumnType)
			{
			case ColumnType.Int24:
			case ColumnType.Long:
				return typeof(int);

			case ColumnType.String:
			case ColumnType.VarString:
				return typeof(string);

			case ColumnType.Short:
				return typeof(short);

			default:
				throw new NotImplementedException(Invariant($"GetFieldType for {m_columnDefinitions[ordinal].ColumnType} is not implemented"));
			}
		}

		public override object GetValue(int ordinal)
		{
			VerifyRead();
			if (ordinal < 0 || ordinal > m_columnDefinitions.Length)
				throw new ArgumentOutOfRangeException(nameof(ordinal), Invariant($"value must be between 0 and {m_columnDefinitions.Length}."));

			if (m_dataOffsets[ordinal] == -1)
				return DBNull.Value;

			var data = new ArraySegment<byte>(m_currentRow, m_dataOffsets[ordinal], m_dataLengths[ordinal]);
			switch (m_columnDefinitions[ordinal].ColumnType)
			{
			case ColumnType.Int24:
			case ColumnType.Long:
				return int.Parse(Encoding.UTF8.GetString(data), CultureInfo.InvariantCulture);

			case ColumnType.String:
			case ColumnType.VarString:
				return Encoding.UTF8.GetString(data);

			case ColumnType.Short:
				return short.Parse(Encoding.UTF8.GetString(data), CultureInfo.InvariantCulture);

			default:
				throw new NotImplementedException(Invariant($"Reading {m_columnDefinitions[ordinal].ColumnType} not implemented"));
			}
		}

		public override IEnumerator GetEnumerator()
		{
			throw new NotSupportedException();
		}

		public override int Depth
		{
			get { throw new NotSupportedException(); }
		}

		protected override DbDataReader GetDbDataReader(int ordinal)
		{
			throw new NotSupportedException();
		}

		public override int VisibleFieldCount => FieldCount;

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					Reset();
					m_session = null;

					if (m_behavior.HasFlag(CommandBehavior.CloseConnection))
					{
						var dbConnection = m_command.Connection;
						m_command.Dispose();
						dbConnection.Dispose();
					}

					m_command = null;
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		}
		
		internal static async Task<DbDataReader> CreateAsync(MySqlCommand command, CommandBehavior behavior, CancellationToken cancellationToken)
		{
			var dataReader = new MySqlDataReader(command, behavior);
			await dataReader.ReadResultSetHeader(cancellationToken).ConfigureAwait(false);
			return dataReader;
		}

		private MySqlDataReader(MySqlCommand command, CommandBehavior behavior)
		{
			m_command = command;
			m_session = ((MySqlConnection) m_command.Connection).Session;
			m_behavior = behavior;
		}

		private async Task ReadResultSetHeader(CancellationToken cancellationToken)
		{
			var payload = await m_session.ReceiveReplyAsync(cancellationToken).ConfigureAwait(false);

			var firstByte = payload.ArraySegment.Array[payload.ArraySegment.Offset];
			if (firstByte == 0)
			{
				var ok = OkPayload.Create(payload);
				m_recordsAffected += ok.AffectedRowCount;
				m_state = ok.ServerStatus.HasFlag(ServerStatus.MoreResultsExist) ? State.HasMoreData : State.NoMoreData;
			}
			else if (firstByte == 0xFB)
			{
				throw new NotSupportedException("Don't support LOCAL_INFILE_Request");
			}
			else
			{
				var reader = new ByteArrayReader(payload.ArraySegment);
				var columnCount = (int) reader.ReadLengthEncodedInteger();
				m_columnDefinitions = new ColumnDefinitionPayload[columnCount];
				m_dataOffsets = new int[columnCount];
				m_dataLengths = new int[columnCount];

				for (var column = 0; column < m_columnDefinitions.Length; column++)
				{
					payload = await m_session.ReceiveReplyAsync(cancellationToken).ConfigureAwait(false);
					m_columnDefinitions[column] = ColumnDefinitionPayload.Create(payload);
				}

				payload = await m_session.ReceiveReplyAsync(cancellationToken).ConfigureAwait(false);
				if (payload.ArraySegment.Array[payload.ArraySegment.Offset] != 0xFE)
					throw new FormatException("Expected EOF packet following column data.");

				m_state = State.ReadResultSetHeader;
			}

			m_currentStatementIndex++;
		}


		private void Reset()
		{
			m_columnDefinitions = null;
			m_dataOffsets = null;
			m_dataLengths = null;
			m_state = State.None;
		}

		private void VerifyHasResult()
		{
			VerifyNotDisposed();
			if (m_state != State.ReadResultSetHeader && m_state != State.ReadingRows)
				throw new InvalidOperationException("There is no current result set.");
		}

		private void VerifyRead()
		{
			VerifyHasResult();
			if (m_state != State.ReadingRows)
				throw new InvalidOperationException("Read must be called first.");
		}

		private void VerifyNotDisposed()
		{
			if (m_command == null)
				throw new ObjectDisposedException(GetType().Name);
		}

		private static Task<bool> CreateCanceledTask()
		{
			var source = new TaskCompletionSource<bool>();
			source.SetCanceled();
			return source.Task;
		}

		private enum State
		{
			None,
			ReadResultSetHeader,
			AlreadyReadFirstRow,
			ReadingRows,
			HasMoreData,
			NoMoreData,
		}

		MySqlCommand m_command;
		MySqlSession m_session;
		State m_state;
		readonly CommandBehavior m_behavior;
		int m_recordsAffected;
		int m_currentStatementIndex;
		ColumnDefinitionPayload[] m_columnDefinitions;
		int[] m_dataOffsets;
		int[] m_dataLengths;
		byte[] m_currentRow;
	}
}