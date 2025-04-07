using Microsoft.Extensions.Options;
using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;

namespace RevealSdk.Server.Reveal
{
    internal class DataSourceProvider : IRVDataSourceProvider
    {
        private readonly ConnectionSettings _connectionSettings;

        public DataSourceProvider(ConnectionSettings connectionSettings)
        {
            _connectionSettings = connectionSettings;
        }

        public async Task<RVDataSourceItem>? ChangeDataSourceItemAsync(IRVUserContext userContext,
            string dashboardId, RVDataSourceItem dataSourceItem)
        {
            if (dataSourceItem is RVSqlServerDataSourceItem sqlDsi)
            {
                await ChangeDataSourceAsync(userContext, sqlDsi.DataSource);
                var newQuery = QueryStore.SqlQuery.Replace(";", "");
                sqlDsi.CustomQuery = newQuery; 
            }
            QueryStore.SqlQuery = "";
            return await Task.FromResult(dataSourceItem);
        }

        public Task<RVDashboardDataSource> ChangeDataSourceAsync(IRVUserContext userContext, RVDashboardDataSource dataSource)
        {
            if (dataSource is RVSqlServerDataSource sqlDs)
            {
                sqlDs.Host = _connectionSettings.Host;
                sqlDs.Database = _connectionSettings.Database;                
            }
            return Task.FromResult(dataSource);
        }
    }
}