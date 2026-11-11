using System.Text;
using Xunit;

namespace WinNewsWire.Database.Tests;

public sealed class DatabaseStringTests
{
    [Fact]
    public void NativeStringInputsRoundTripWithTheirUtf8Bytes()
    {
        string[] inputs =
        [
            "Hello World",
            "Caf\u00e9 \u2014 \u00dcber alles",
            "Launch day \U0001f680\U0001f389",
            "\u4f60\u597d\u4e16\u754c \u3053\u3093\u306b\u3061\u306f",
            LongMixedBody()
        ];
        using var queue = new DatabaseQueue(":memory:");
        queue.Run(connection =>
        {
            connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, s TEXT);");
            for (int index = 0; index < inputs.Length; index++)
                Assert.Equal(1, connection.Execute("INSERT INTO t (id, s) VALUES ($id, $s);", ("$id", index), ("$s", inputs[index])));
            using var command = connection.Cmd("SELECT id, s, CAST(s AS BLOB) FROM t ORDER BY id;");
            using var rows = command.ExecuteReader();
            int count = 0;
            while (rows.Read())
            {
                int index = rows.GetInt32(0);
                Assert.Equal(inputs[index], rows.GetStringOrNull(1));
                Assert.Equal(Encoding.UTF8.GetBytes(inputs[index]), rows.GetFieldValue<byte[]>(2));
                count++;
            }
            Assert.Equal(inputs.Length, count);
        });
    }

    [Fact]
    public void NativeSqlNullAndEmptyTextRemainDistinct()
    {
        using var queue = new DatabaseQueue(":memory:");
        queue.Run(connection =>
        {
            connection.Execute("""
                CREATE TABLE t (id INTEGER PRIMARY KEY, s TEXT);
                INSERT INTO t (id, s) VALUES (0, NULL);
                INSERT INTO t (id, s) VALUES (1, '');
                """);
            using var command = connection.Cmd("SELECT id, s FROM t ORDER BY id;");
            using var rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.Null(rows.GetStringOrNull(1));
            Assert.True(rows.Read());
            Assert.Equal("", rows.GetStringOrNull(1));
            Assert.False(rows.Read());
        });
    }

    private static string LongMixedBody()
    {
        string[] pieces =
        [
            "The quick brown fox jumps over the lazy dog. ",
            "Caf\u00e9 \u2014 r\u00e9sum\u00e9 d\u00e9j\u00e0-vu na\u00efvet\u00e9 fa\u00e7ade jalape\u00f1o pi\u00f1ata. ",
            "Launch day \U0001f680\U0001f389\U0001f30d \u2014 feedback welcome. ",
            "\u4f60\u597d\u4e16\u754c \u3053\u3093\u306b\u3061\u306f \uc548\ub155\ud558\uc138\uc694. "
        ];
        var body = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(body.ToString()) < 2048)
            foreach (string piece in pieces) body.Append(piece);
        return body.ToString();
    }
}
