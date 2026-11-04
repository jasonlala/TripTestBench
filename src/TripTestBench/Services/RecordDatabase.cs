// ============================================================================
// 文件：RecordDatabase.cs
// 作用：SQLite 试验数据库的底层访问（建表、写入、查询、读取截图）。
//       数据库文件：%LOCALAPPDATA%\TripTestBench\triptestbench.db
//       表结构：TestRecords —— 一条记录一行，最后两列为说明字段与曲线截图：
//               · Remark      TEXT  备注
//               · ChartImage  BLOB  本次试验的电流曲线截图（PNG 原始字节）
//       设计要点：
//       1) 列表查询不读取 ChartImage，避免一次性把大量图片载入内存；
//          需要看图时用 GetChartImage(id) 按主键单独取；
//       2) 查询额外返回 HasChart 标记，界面据此显示“查看曲线”入口；
//       3) 时间以 "yyyy-MM-dd HH:mm:ss" 文本存储，字典序即时间序，可直接排序。
// ============================================================================

using System.Globalization;       // 固定时间格式（不受系统区域设置影响）
using System.IO;                  // Path / Directory
using Microsoft.Data.Sqlite;      // SQLite 驱动
using TripTestBench.Models;       // TestRecord

namespace TripTestBench.Services;

/// <summary>SQLite 试验数据库访问层。</summary>
public sealed class RecordDatabase
{
    /// <summary>时间字段的存储格式：字典序与时间序一致，便于 ORDER BY。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string _connectionString; // 连接字符串（每次操作新建连接，短连接更稳）

    /// <summary>默认构造：使用 AppPaths.DatabaseFile。</summary>
    public RecordDatabase()
        : this(AppPaths.DatabaseFile)
    {
    }

    /// <summary>指定数据库文件构造（便于测试或多库切换）。</summary>
    public RecordDatabase(string databaseFile)
    {
        AppPaths.EnsureDataDir();

        var directory = Path.GetDirectoryName(databaseFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FilePath = databaseFile;

        // ReadWriteCreate：文件不存在时自动创建；Shared 缓存减少多连接锁冲突
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        EnsureSchema();
    }

    /// <summary>数据库文件完整路径（日志与界面提示用）。</summary>
    public string FilePath { get; }

    /// <summary>打开一个连接（调用方负责释放）。</summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>建表与索引：已存在则跳过，保证程序可重复启动。</summary>
    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        // ChartImage 放在最后一列，符合“数据库最后一个字段保存图表截图”的要求
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS TestRecords (
                Id             TEXT    NOT NULL PRIMARY KEY,
                TestTime       TEXT    NOT NULL,
                ProductName    TEXT    NOT NULL DEFAULT '',
                ProductCode    TEXT    NOT NULL DEFAULT '',
                Tester         TEXT    NOT NULL DEFAULT '',
                Station        INTEGER NOT NULL DEFAULT 1,
                SetCurrentKa   REAL    NOT NULL DEFAULT 0,
                SetTimeSeconds REAL    NOT NULL DEFAULT 0,
                TripCurrentKa  REAL    NOT NULL DEFAULT 0,
                PeakCurrentKa  REAL    NOT NULL DEFAULT 0,
                RunTimeSeconds REAL    NOT NULL DEFAULT 0,
                Passed         INTEGER NOT NULL DEFAULT 0,
                Remark         TEXT    NOT NULL DEFAULT '',
                ChartImage     BLOB
            );

            CREATE INDEX IF NOT EXISTS IX_TestRecords_TestTime
                ON TestRecords (TestTime DESC);
            """;

        command.ExecuteNonQuery();
    }

    /// <summary>当前记录总数（迁移判断与界面统计用）。</summary>
    public int Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM TestRecords";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 插入一条记录（含曲线截图）。
    /// </summary>
    /// <param name="record">试验记录</param>
    /// <param name="chartImage">曲线截图 PNG 字节；为空表示本次未截图</param>
    public void Insert(TestRecord record, byte[]? chartImage)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO TestRecords
                (Id, TestTime, ProductName, ProductCode, Tester, Station,
                 SetCurrentKa, SetTimeSeconds, TripCurrentKa, PeakCurrentKa,
                 RunTimeSeconds, Passed, Remark, ChartImage)
            VALUES
                ($id, $time, $name, $code, $tester, $station,
                 $setCurrent, $setTime, $trip, $peak,
                 $runTime, $passed, $remark, $chart)
            """;

        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$time", record.TestTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$name", record.ProductName ?? string.Empty);
        command.Parameters.AddWithValue("$code", record.ProductCode ?? string.Empty);
        command.Parameters.AddWithValue("$tester", record.Tester ?? string.Empty);
        command.Parameters.AddWithValue("$station", record.Station);
        command.Parameters.AddWithValue("$setCurrent", record.SetCurrentKa);
        command.Parameters.AddWithValue("$setTime", record.SetTimeSeconds);
        command.Parameters.AddWithValue("$trip", record.TripCurrentKa);
        command.Parameters.AddWithValue("$peak", record.PeakCurrentKa);
        command.Parameters.AddWithValue("$runTime", record.RunTimeSeconds);
        command.Parameters.AddWithValue("$passed", record.Passed ? 1 : 0);
        command.Parameters.AddWithValue("$remark", record.Remark ?? string.Empty);
        command.Parameters.AddWithValue("$chart", (object?)chartImage ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 查询全部记录（按试验时间倒序），不读取 ChartImage，只带“是否有截图”标记。
    /// </summary>
    public List<TestRecord> QueryAll()
    {
        var list = new List<TestRecord>();

        using var connection = Open();
        using var command = connection.CreateCommand();

        // CASE 判断截图是否存在，避免把 BLOB 整块读出来
        command.CommandText = """
            SELECT Id, TestTime, ProductName, ProductCode, Tester, Station,
                   SetCurrentKa, SetTimeSeconds, TripCurrentKa, PeakCurrentKa,
                   RunTimeSeconds, Passed, Remark,
                   CASE WHEN ChartImage IS NULL OR length(ChartImage) = 0 THEN 0 ELSE 1 END AS HasChart
            FROM TestRecords
            ORDER BY TestTime DESC
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TestRecord
            {
                Id = reader.GetString(0),
                TestTime = ParseTime(reader.GetString(1)),
                ProductName = reader.GetString(2),
                ProductCode = reader.GetString(3),
                Tester = reader.GetString(4),
                Station = reader.GetInt32(5),
                SetCurrentKa = reader.GetDouble(6),
                SetTimeSeconds = reader.GetDouble(7),
                TripCurrentKa = reader.GetDouble(8),
                PeakCurrentKa = reader.GetDouble(9),
                RunTimeSeconds = reader.GetDouble(10),
                Passed = reader.GetInt32(11) != 0,
                Remark = reader.GetString(12),
                HasChartImage = reader.GetInt32(13) != 0
            });
        }

        return list;
    }

    /// <summary>按主键读取曲线截图（PNG 字节）；不存在返回 null。</summary>
    public byte[]? GetChartImage(string recordId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT ChartImage FROM TestRecords WHERE Id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", recordId);

        var result = command.ExecuteScalar();
        return result is byte[] bytes && bytes.Length > 0 ? bytes : null;
    }

    /// <summary>解析存储的时间文本；解析失败时退回当前时间，避免整行读取失败。</summary>
    private static DateTime ParseTime(string text)
        => DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : DateTime.Now;
}
