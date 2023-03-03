using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Dodo.Primitives;
using MySqlConnector;
using Prometheus;

public record TestDescription(string KeyType, string OperationType);

public class SqlMethods
{
    private readonly string _connectionString;
    private readonly ILogger<SqlMethods> _logger;

    public string Name { get; private set; }

    public SqlMethods(string name, string connectionString, ILogger<SqlMethods> logger)
    {
        Name = name;
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<int> ExecuteNonQueryAsync(string commandText, CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(_connectionString, cancellationToken);
        var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.Text;
        cmd.CommandText = commandText;
        var result = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private async Task<MySqlConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}

public interface IKeyGeneratorStrategy
{
    string GetName();
    void FillKey(Span<byte> bytes);
}

public class GuidKeyGeneratorStrategy : IKeyGeneratorStrategy
{
    public string GetName()
    {
        return "Guid";
    }

    public void FillKey(Span<byte> bytes)
    {
        var guid = Guid.NewGuid();
        guid.TryWriteBytes(bytes);
    }
}

public class SeqKeyGeneratorStrategy : IKeyGeneratorStrategy
{
    public string GetName()
    {
        return "Primitives_Uuid";
    }

    public void FillKey(Span<byte> bytes)
    {
        var id = Uuid.NewMySqlOptimized();
        id.TryWriteBytes(bytes);
    }
}


public class BrokenKeyGeneratorStrategy : IKeyGeneratorStrategy
{
    private static readonly DateTime Start = DateTime.UtcNow;

    private static DateTime GetScaledUtcNow()
    {
        const double sf = 1_000_000.0;
        return Start + (DateTime.Now - Start) * sf;
    }

    public string GetName()
    {
        return "Uuid_As_Broken_Guid";
    }

    public void FillKey(Span<byte> bytes)
    {
        var id = HackedUuid.NewMySqlOptimized(GetScaledUtcNow());
        var broken = new Guid(id.ToString());
        broken.TryWriteBytes(bytes);
    }
}


public class TableGateway
{
    private readonly string _tableName;
    private readonly SqlMethods _sqlMethods;

    public TableGateway(string tableName, SqlMethods sqlMethods)
    {
        _tableName = tableName;
        _sqlMethods = sqlMethods;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        //await DropDatabase(ct);
        await CreateDbIfNotExists(ct);
        await CreateTestTableIfNotExists(ct);
    }

    public async Task InsertAsync((long index, byte[] key)[] items, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(200)
            .Append("insert into ")
            .Append("test_db.")
            .Append(_tableName)
            .Append(" (id, inc, name) values ")
            .AppendJoin(", ", items.Select(i => $"(0x{Convert.ToHexString(i.key)},{i.index},{RandomNumberGenerator.GetInt32(int.MaxValue).ToString()})"))
            .Append(";");
        await _sqlMethods.ExecuteNonQueryAsync(sb.ToString(), cancellationToken);
    }

    public async Task SelectAsync(CancellationToken cancellationToken)
    {
        await _sqlMethods.ExecuteNonQueryAsync($"select count(*) from (select * from test_db.{_tableName} where name like '%1%' order by inc limit 50000) as a;", cancellationToken);
    }

    private async Task CreateDbIfNotExists(CancellationToken cancellationToken)
    {
        var result = await _sqlMethods.ExecuteNonQueryAsync("CREATE DATABASE IF NOT EXISTS test_db;", cancellationToken);
        if (result != 1)
        {
            throw new Exception($"Result is {result}, expected 1.");
        }
    }

    private async Task CreateTestTableIfNotExists(CancellationToken ct)
    {
        await _sqlMethods.ExecuteNonQueryAsync($"create table if not exists test_db.{_tableName}(id binary(16) not null primary key, inc bigint not null, name nvarchar(50) not null)", ct);
        await _sqlMethods.ExecuteNonQueryAsync($"create index {_tableName}_idx_inc on test_db.{_tableName} (inc)", ct);
    }
}

public interface ITestOperation
{
    public Task Execute(CancellationToken cancellationToken);
}

public class InsertionTestOperation : ITestOperation
{
    private readonly TableGateway _tableGateway;
    private readonly long _index;
    private readonly IKeyGeneratorStrategy _keyGeneratorStrategy;

    public InsertionTestOperation(long index, IKeyGeneratorStrategy keyGeneratorStrategy, TableGateway tableGateway)
    {
        _tableGateway = tableGateway;
        _index = index;
        _keyGeneratorStrategy = keyGeneratorStrategy;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        await _tableGateway.InsertAsync(Enumerable.Range(0, 100).Select(i =>
        {
            var keyBytes = new byte[16];
            _keyGeneratorStrategy.FillKey(keyBytes);
            return (_index * 100 + i, keyBytes);
        }).ToArray(), cancellationToken);
    }
}

public class SelectionTestOperation : ITestOperation
{
    private readonly TableGateway _tableGateway;

    public SelectionTestOperation(TableGateway tableGateway)
    {
        _tableGateway = tableGateway;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        await _tableGateway.SelectAsync(cancellationToken);
    }
}

public interface ITestOperationRun
{
    TestDescription GetDescription();
    ITestOperation Create();
}

public class InsertTestOperationRun : ITestOperationRun
{
    private long _index;
    private readonly IKeyGeneratorStrategy _keyGeneratorStrategy;
    private readonly TableGateway _tableGateway;

    public InsertTestOperationRun(IKeyGeneratorStrategy keyGeneratorStrategy, TableGateway tableGateway)
    {
        _keyGeneratorStrategy = keyGeneratorStrategy;
        _tableGateway = tableGateway;
    }

    public TestDescription GetDescription() => new(_keyGeneratorStrategy.GetName(), "insert");

    public ITestOperation Create() => new InsertionTestOperation(_index++, _keyGeneratorStrategy, _tableGateway);
}

public class SelectTestOperationRun : ITestOperationRun
{
    private readonly IKeyGeneratorStrategy _keyGeneratorStrategy;
    private readonly TableGateway _tableGateway;

    public SelectTestOperationRun(IKeyGeneratorStrategy keyGeneratorStrategy, TableGateway tableGateway)
    {
        _keyGeneratorStrategy = keyGeneratorStrategy;
        _tableGateway = tableGateway;
    }

    public TestDescription GetDescription() => new(_keyGeneratorStrategy.GetName(), "select");

    public ITestOperation Create() => new SelectionTestOperation(_tableGateway);
}

public class LoadTestCase
{
    private readonly ITestOperationRun _testOperationRun;
    private readonly ILogger<LoadTestCase> _logger;

    private readonly Channel<ITestOperation> _testOperations = Channel.CreateBounded<ITestOperation>(30);

    public LoadTestCase(ITestOperationRun testOperationRun, ILogger<LoadTestCase> logger)
    {
        _testOperationRun = testOperationRun;
        _logger = logger;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        async Task Generate()
        {
            while (! cancellationToken.IsCancellationRequested)
            {
                await _testOperations.Writer.WriteAsync(_testOperationRun.Create(), cancellationToken);
            }
        }

        var tasks = new List<Task>();
        var generator = Generate();
        tasks.Add(generator);

        var counter = Metrics.CreateCounter("uuid_variants_throughput_3", "Operation Throughput",
            "key_type", "operation_type");

        var testDescription = _testOperationRun.GetDescription();

        async Task ExecuteOperationsJob()
        {
            await foreach (var op in _testOperations.Reader.ReadAllAsync(cancellationToken))
            {
                bool cooldown = false;
                try
                {
                    await op.Execute(cancellationToken);

                    counter
                        .WithLabels(testDescription.KeyType, testDescription.OperationType)
                        .Inc();
                }
                catch (Exception e)
                {
                    cooldown = true;
                    _logger.LogError(e, "Error executing operation.");
                }

                if (cooldown)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        var executors = Enumerable.Range(0, 30).Select(i => ExecuteOperationsJob()).ToList();
        tasks.AddRange(executors);
        await Task.WhenAll(tasks);
    }
}

public class TestSetupService : BackgroundService
{
    private ImmutableDictionary<string, SqlMethods> _sqlMethods;
    private readonly ILoggerFactory _loggerFactory;

    public TestSetupService(IEnumerable<SqlMethods> sqlMethods, ILoggerFactory loggerFactory)
    {
        _sqlMethods = sqlMethods.ToImmutableDictionary(x => x.Name);
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await DropDatabase(cancellationToken);
        //var guidKeysTable = await InitializeNewTableGatewayAsync(cancellationToken, "guid_keys");
        var seqKeysTable = await InitializeNewTableGatewayAsync(cancellationToken, "seq_keys");
        var brokenKeysTable = await InitializeNewTableGatewayAsync(cancellationToken, "broken_keys");
        var tasks = new List<Task>
        {
            //CreateGuidInsertionTestCase(guidKeysTable, cancellationToken),
            //CreateGuidSelectionTestCase(guidKeysTable, cancellationToken),
            CreateSeqInsertionTestCase(seqKeysTable, cancellationToken),
            CreateSeqSelectionTestCase(seqKeysTable, cancellationToken),
            CreateBrokenInsertionTestCase(brokenKeysTable, cancellationToken),
            CreateBrokenSelectionTestCase(brokenKeysTable, cancellationToken)
        };

        await Task.WhenAll(tasks);
    }

    private async Task CreateGuidInsertionTestCase(TableGateway tableGateway, CancellationToken cancellationToken)
    {
        var keyGenerator = new GuidKeyGeneratorStrategy();
        await new LoadTestCase(new InsertTestOperationRun(keyGenerator, tableGateway), _loggerFactory.CreateLogger<LoadTestCase>()).Execute(cancellationToken);
    }

    private async Task CreateGuidSelectionTestCase(TableGateway tableGateway, CancellationToken cancellationToken)
    {
        var keyGenerator = new GuidKeyGeneratorStrategy();
        await new LoadTestCase(new SelectTestOperationRun(keyGenerator, tableGateway), _loggerFactory.CreateLogger<LoadTestCase>()).Execute(cancellationToken);
    }

    private async Task CreateSeqInsertionTestCase(TableGateway tableGateway, CancellationToken cancellationToken)
    {
        var keyGenerator = new SeqKeyGeneratorStrategy();
        await new LoadTestCase(new InsertTestOperationRun(keyGenerator, tableGateway), _loggerFactory.CreateLogger<LoadTestCase>()).Execute(cancellationToken);
    }

    private async Task CreateSeqSelectionTestCase(TableGateway tableGateway, CancellationToken cancellationToken)
    {
        var keyGenerator = new SeqKeyGeneratorStrategy();
        await new LoadTestCase(new SelectTestOperationRun(keyGenerator, tableGateway), _loggerFactory.CreateLogger<LoadTestCase>()).Execute(cancellationToken);
    }

    private async Task CreateBrokenInsertionTestCase(TableGateway tableGateway, CancellationToken cancellationToken)
    {
        var keyGenerator = new BrokenKeyGeneratorStrategy();
        await new LoadTestCase(new InsertTestOperationRun(keyGenerator, tableGateway), _loggerFactory.CreateLogger<LoadTestCase>()).Execute(cancellationToken);
    }

    private async Task CreateBrokenSelectionTestCase(TableGateway tableGateway, CancellationToken cancellationToken)
    {
        var keyGenerator = new BrokenKeyGeneratorStrategy();
        await new LoadTestCase(new SelectTestOperationRun(keyGenerator, tableGateway), _loggerFactory.CreateLogger<LoadTestCase>()).Execute(cancellationToken);
    }

    private async Task<TableGateway> InitializeNewTableGatewayAsync(CancellationToken cancellationToken, string tableName)
    {
        var tableGateway = new TableGateway(tableName, _sqlMethods[tableName]);
        await tableGateway.InitializeAsync(cancellationToken);
        return tableGateway;
    }

    private async Task DropDatabase(CancellationToken ct)
    {
        foreach (var db in _sqlMethods.Values)
        {
            await db.ExecuteNonQueryAsync("drop database if exists test_db;", ct);
        }
    }
}