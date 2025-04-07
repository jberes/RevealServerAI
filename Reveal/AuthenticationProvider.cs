
using Microsoft.Extensions.Options;
using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Microsoft.SqlServer;

namespace RevealSdk.Server.Reveal
{
    public class AuthenticationProvider : IRVAuthenticationProvider
    {
        private readonly ConnectionSettings _connectionSettings;

        public AuthenticationProvider(ConnectionSettings connectionSettings)
        {
            _connectionSettings = connectionSettings;
        }
        public Task<IRVDataSourceCredential> ResolveCredentialsAsync(IRVUserContext userContext,
            RVDashboardDataSource dataSource)
        {        
            IRVDataSourceCredential userCredential = new RVIntegratedAuthenticationCredential();
            
            if (dataSource is RVSqlServerDataSource)
            {
                userCredential = new RVUsernamePasswordDataSourceCredential(_connectionSettings.DatabaseUserName, _connectionSettings.DatabasePassword);
            }
            return Task.FromResult(userCredential);
        }
    }
}

