namespace CmsEventProcessor.IntegrationTests.Sql;

/// <summary>
/// Shares one disposable SQL Server instance while isolating each test in its own database.
/// </summary>
[CollectionDefinition("SQL", DisableParallelization = true)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;
