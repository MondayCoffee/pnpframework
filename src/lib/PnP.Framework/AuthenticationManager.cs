﻿using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensibility;
using Microsoft.SharePoint.Client;
using PnP.Framework.Diagnostics;
using PnP.Framework.Utilities.Context;
using System;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

#if DEBUG
[assembly: InternalsVisibleTo("PnP.Framework.Test")]
#endif
namespace PnP.Framework
{
    /// <summary>
    /// Enum to identify the supported Office 365 hosting environments
    /// </summary>
    public enum AzureEnvironment
    {
        /// <summary>
        /// 
        /// </summary>
        Production = 0,
        /// <summary>
        /// 
        /// </summary>
        PPE = 1,
        /// <summary>
        /// 
        /// </summary>
        China = 2,
        /// <summary>
        /// 
        /// </summary>
        Germany = 3,
        /// <summary>
        /// 
        /// </summary>
        USGovernment = 4
    }


    /// <summary>
    /// A Known Client Ids to use for authentication
    /// </summary>
    public enum KnownClientId
    {
        /// <summary>
        /// 
        /// </summary>
        PnPManagementShell,
        /// <summary>
        /// 
        /// </summary>
        SPOManagementShell
    }

    /// <summary>
    /// This manager class can be used to obtain a SharePoint Client Context object
    /// </summary>
    public class AuthenticationManager : IDisposable
    {
        private const string SHAREPOINT_PRINCIPAL = "00000003-0000-0ff1-ce00-000000000000";
        /// <summary>
        /// The client id of the Microsoft SharePoint Online Management Shell application
        /// </summary>
        public const string CLIENTID_SPOMANAGEMENTSHELL = "9bc3ab49-b65d-410a-85ad-de819febfddc";
        /// <summary>
        /// The client id of the Microsoft 365 Patters and Practices Management Shell application
        /// </summary>
        public const string CLIENTID_PNPMANAGEMENTSHELL = "31359c7f-bd7e-475c-86db-fdb8c937548e";

        private string appOnlyAccessToken;
        private AutoResetEvent appOnlyACSAccessTokenResetEvent;
        private readonly object tokenLock = new object();
        private bool disposedValue;

        private readonly IPublicClientApplication publicClientApplication;
        private readonly IConfidentialClientApplication confidentialClientApplication;
        private readonly string azureADEndPoint;
        private readonly ClientContextType authenticationType;
        private readonly string username;
        private readonly SecureString password;
        private readonly UserAssertion assertion;
        private readonly Func<DeviceCodeResult, Task> deviceCodeCallback;
        private readonly ICustomWebUi customWebUi;

        internal string RedirectUrl { get; set; }

        #region Construction
        /// <summary>
        /// Empty constructor, to be used if you want to execute ACS based authentication methods.
        /// </summary>
        public AuthenticationManager()
        {
            // Set the TLS preference. Needed on some server os's to work when Office 365 removes support for TLS 1.0
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContexts. It uses the PnP Management Shell multi-tenant Azure AD application ID to authenticate. By default tokens will be cached in memory.
        /// </summary>
        /// <param name="username">The username to use for authentication</param>
        /// <param name="password">The password to use for authentication</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string username, SecureString password, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this(GetKnownClientId(KnownClientId.PnPManagementShell), username, password, "https://login.microsoftonline.com/common/oauth2/nativeclient", azureEnvironment, tokenCacheCallback)
        {
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContexts.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="username">The username to use for authentication</param>
        /// <param name="password">The password to use for authentication</param>
        /// <param name="redirectUrl">Optional redirect URL to use for authentication as set up in the Azure AD Application</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string clientId, string username, SecureString password, string redirectUrl = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this()
        {
            azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);

            var builder = PublicClientApplicationBuilder.Create(clientId).WithAuthority($"{azureADEndPoint}/organizations/");
            if (!string.IsNullOrEmpty(redirectUrl))
            {
                builder = builder.WithRedirectUri(redirectUrl);
            }
            this.username = username;
            this.password = password;
            publicClientApplication = builder.Build();

            // register tokencache if callback provided
            tokenCacheCallback?.Invoke(publicClientApplication.UserTokenCache);
            authenticationType = ClientContextType.AzureADCredentials;
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire access tokens and client contexts using the Azure AD Interactive flow.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="openBrowserCallback">This callback will be called providing the URL and port to open during the authentication flow</param>
        /// <param name="tenantId">Optional tenant id or tenant url</param>
        /// <param name="successMessageHtml">Allows you to override the success message. Notice that a success header message will be added.</param>
        /// <param name="failureMessageHtml">llows you to override the failure message. Notice that a failed header message will be added and the error message will be appended.</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called to register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string clientId, Action<string, int> openBrowserCallback, string tenantId = null, string successMessageHtml = null, string failureMessageHtml = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this(clientId,Utilities.OAuth.DefaultBrowserUi.FindFreeLocalhostRedirectUri(),tenantId,azureEnvironment,tokenCacheCallback, new Utilities.OAuth.DefaultBrowserUi(openBrowserCallback, successMessageHtml, failureMessageHtml))
        {
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire access tokens and client contexts using the Azure AD Interactive flow.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="redirectUrl">Optional redirect URL to use for authentication as set up in the Azure AD Application</param>
        /// <param name="tenantId">Optional tenant id or tenant url</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        /// <param name="customWebUi">Optional ICusomtWebUi object to fully customize the feedback behavior</param>
        public AuthenticationManager(string clientId, string redirectUrl = null, string tenantId = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null, ICustomWebUi customWebUi = null) : this()
        {
            azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);
            var builder = PublicClientApplicationBuilder.Create(clientId).WithAuthority($"{azureADEndPoint}/organizations/");
            if (!string.IsNullOrEmpty(redirectUrl))
            {
                builder = builder.WithRedirectUri(redirectUrl);
            }
            if (!string.IsNullOrEmpty(tenantId))
            {
                builder = builder.WithTenantId(tenantId);
            }
            publicClientApplication = builder.Build();
            this.customWebUi = customWebUi;
            // register tokencache if callback provided
            tokenCacheCallback?.Invoke(publicClientApplication.UserTokenCache);

            authenticationType = ClientContextType.AzureADInteractive;
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContexts through device code authentication
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="deviceCodeCallback">The callback that will be called with device code information.</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string clientId, Func<DeviceCodeResult, Task> deviceCodeCallback, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this()
        {
            azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);
            this.deviceCodeCallback = deviceCodeCallback;

            var builder = PublicClientApplicationBuilder.Create(clientId).WithAuthority($"{azureADEndPoint}/organizations/");

            publicClientApplication = builder.Build();

            // register tokencache if callback provided
            tokenCacheCallback?.Invoke(publicClientApplication.UserTokenCache);

            authenticationType = ClientContextType.DeviceLogin;
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContexts.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="certificate">A valid certificate</param>
        /// <param name="tenantId">Tenant id or tenant url</param>
        /// <param name="redirectUrl">Optional redirect URL to use for authentication as set up in the Azure AD Application</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string clientId, X509Certificate2 certificate, string tenantId, string redirectUrl = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this()
        {
            azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);
            ConfidentialClientApplicationBuilder builder;
            if (azureEnvironment != AzureEnvironment.Production)
            {
                builder = ConfidentialClientApplicationBuilder.Create(clientId).WithCertificate(certificate).WithTenantId(tenantId).WithAuthority(azureADEndPoint, tenantId, true);
            }
            else
            {
                builder = ConfidentialClientApplicationBuilder.Create(clientId).WithCertificate(certificate).WithTenantId(tenantId);
            }

            if (!string.IsNullOrEmpty(redirectUrl))
            {
                builder = builder.WithRedirectUri(redirectUrl);
            }

            confidentialClientApplication = builder.Build();

            // register tokencache if callback provided
            tokenCacheCallback?.Invoke(confidentialClientApplication.UserTokenCache);

            authenticationType = ClientContextType.AzureADCertificate;
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContexts.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="certificatePath">A valid path to a certificate file</param>
        /// <param name="certificatePassword">The password for the certificate</param>
        /// <param name="tenantId">The tenant id (guid) or name (e.g. contoso.onmicrosoft.com) </param>
        /// <param name="redirectUrl">Optional redirect URL to use for authentication as set up in the Azure AD Application</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string clientId, string certificatePath, string certificatePassword, string tenantId, string redirectUrl = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this()
        {
            azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);

            if (System.IO.File.Exists(certificatePath))
            {
                ConfidentialClientApplicationBuilder builder = null;

                using (var certfile = System.IO.File.OpenRead(certificatePath))
                {
                    var certificateBytes = new byte[certfile.Length];
                    certfile.Read(certificateBytes, 0, (int)certfile.Length);
                    // Don't dispose the cert as that will lead to "m_safeCertContext is an invalid handle" errors when the confidential client actually uses the cert
#pragma warning disable CA2000 // Dispose objects before losing scope
                    var certificate = new X509Certificate2(certificateBytes,
                                                           certificatePassword,
                                                           X509KeyStorageFlags.Exportable |
                                                           X509KeyStorageFlags.MachineKeySet |
                                                           X509KeyStorageFlags.PersistKeySet);
#pragma warning restore CA2000 // Dispose objects before losing scope

                    builder = ConfidentialClientApplicationBuilder.Create(clientId).WithCertificate(certificate).WithTenantId(tenantId);
                }

                if (azureEnvironment != AzureEnvironment.Production)
                {
                    builder.WithAuthority(azureADEndPoint, tenantId, true);
                }

                if (!string.IsNullOrEmpty(redirectUrl))
                {
                    builder = builder.WithRedirectUri(redirectUrl);
                }

                confidentialClientApplication = builder.Build();

                // register tokencache if callback provided. ApptokenCache as AcquireTokenForClient is beind called to acquire tokens.
                tokenCacheCallback?.Invoke(confidentialClientApplication.AppTokenCache);

                authenticationType = ClientContextType.AzureADCertificate;

            }
            else
            {
                throw new Exception("Certificate path not found");
            }
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContexts.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication</param>
        /// <param name="storeName">The name of the certificate store to find the certificate in.</param>
        /// <param name="storeLocation">The location of the certificate store to find the certificate in.</param>
        /// <param name="thumbPrint">The thumbprint of the certificate to use.</param>
        /// <param name="tenantId">The tenant id (guid) or name (e.g. contoso.onmicrosoft.com) </param>
        /// <param name="redirectUrl">Optional redirect URL to use for authentication as set up in the Azure AD Application</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="tokenCacheCallback">If present, after setting up the base flow for authentication this callback will be called register a custom tokencache. See https://aka.ms/msal-net-token-cache-serialization.</param>
        public AuthenticationManager(string clientId, StoreName storeName, StoreLocation storeLocation, string thumbPrint, string tenantId, string redirectUrl = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this()
        {
            azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);

            var certificate = Utilities.X509CertificateUtility.LoadCertificate(storeName, storeLocation, thumbPrint);

            var builder = ConfidentialClientApplicationBuilder.Create(clientId).WithCertificate(certificate).WithTenantId(tenantId);

            if (azureEnvironment != AzureEnvironment.Production)
            {
                builder.WithAuthority(azureADEndPoint, tenantId, true);
            }

            if (!string.IsNullOrEmpty(redirectUrl))
            {
                builder = builder.WithRedirectUri(redirectUrl);
            }

            confidentialClientApplication = builder.Build();

            // register tokencache if callback provided. ApptokenCache as AcquireTokenForClient is beind called to acquire tokens.
            tokenCacheCallback?.Invoke(confidentialClientApplication.AppTokenCache);

            authenticationType = ClientContextType.AzureADCertificate;
        }

        /// <summary>
        /// Creates a new instance of the Authentication Manager to acquire authenticated ClientContext.
        /// </summary>
        /// <param name="clientId">The client id of the Azure AD application to use for authentication.</param>
        /// <param name="clientSecret">The client secret of the Azure AD application to use for authentication.</param>
        /// <param name="tenantId">Optional tenant id or tenant url</param>
        /// <param name="azureEnvironment">The azure environment to use. Defaults to AzureEnvironment.Production</param>
        /// <param name="userAssertion">The user assertion (token) of the user on whose behalf to acquire the context</param>
        /// <param name="tokenCacheCallback"></param>
        public AuthenticationManager(string clientId, string clientSecret, UserAssertion userAssertion, string tenantId = null, AzureEnvironment azureEnvironment = AzureEnvironment.Production, Action<ITokenCache> tokenCacheCallback = null) : this()
        {
            var azureADEndPoint = GetAzureADLoginEndPoint(azureEnvironment);

            ConfidentialClientApplicationBuilder builder;
            if (azureEnvironment != AzureEnvironment.Production)
            {
                if (tenantId == null)
                {
                    throw new ArgumentException("tenantId is required", nameof(tenantId));
                }
                builder = ConfidentialClientApplicationBuilder.Create(clientId).WithClientSecret(clientSecret).WithAuthority(azureADEndPoint, tenantId, true);
            }
            else
            {
                builder = ConfidentialClientApplicationBuilder.Create(clientId).WithClientSecret(clientSecret).WithAuthority($"{azureADEndPoint}/organizations/");
                if (!string.IsNullOrEmpty(tenantId))
                {
                    builder = builder.WithTenantId(tenantId);
                }
            }
            this.assertion = userAssertion;
            confidentialClientApplication = builder.Build();

            // register tokencache if callback provided
            tokenCacheCallback?.Invoke(confidentialClientApplication.UserTokenCache);
            authenticationType = ClientContextType.AzureOnBehalfOf;
        }
        #endregion

        /// <summary>
        /// Returns an access token for a given site.
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <returns></returns>
        public string GetAccessToken(string siteUrl)
        {
            return GetAccessTokenAsync(siteUrl, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns an access token for a given site.
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <returns></returns>
        public async Task<string> GetAccessTokenAsync(string siteUrl)
        {
            return await GetAccessTokenAsync(siteUrl, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns an access token for a given site.
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the request</param>
        /// <returns></returns>
        public string GetAccessToken(string siteUrl, CancellationToken cancellationToken)
        {
            var uri = new Uri(siteUrl);

            var scopes = new[] { $"{uri.Scheme}://{uri.Authority}/.default" };

            return GetAccessTokenAsync(scopes, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns an access token for a given site.
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the request</param>
        /// <returns></returns>
        public async Task<string> GetAccessTokenAsync(string siteUrl, CancellationToken cancellationToken)
        {
            var uri = new Uri(siteUrl);

            var scopes = new[] { $"{uri.Scheme}://{uri.Authority}/.default" };

            return await GetAccessTokenAsync(scopes, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> GetAccessTokenAsync(string[] scopes)
        {
            return await GetAccessTokenAsync(scopes, CancellationToken.None);
        }

        /// <summary>
        /// Returns an access token for a given site.
        /// </summary>
        /// <param name="scopes">The scopes to retrieve the access token for</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the request</param>
        /// <returns></returns>
        public async Task<string> GetAccessTokenAsync(string[] scopes, CancellationToken cancellationToken)
        {
            AuthenticationResult authResult = null;

            switch (authenticationType)
            {
                case ClientContextType.AzureADCredentials:
                    {
                        var accounts = await publicClientApplication.GetAccountsAsync();
                        try
                        {
                            authResult = await publicClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await publicClientApplication.AcquireTokenByUsernamePassword(scopes, username, password).ExecuteAsync(cancellationToken);
                        }
                        break;
                    }
                case ClientContextType.AzureADInteractive:
                    {
                        var accounts = await publicClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await publicClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            var builder = publicClientApplication.AcquireTokenInteractive(scopes);
                            if (customWebUi != null)
                            {
                                builder = builder.WithCustomWebUi(customWebUi);
                            }
                            authResult = await builder.ExecuteAsync(cancellationToken);
                        }
                        break;
                    }
                case ClientContextType.AzureADCertificate:
                    {
                        var accounts = await confidentialClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await confidentialClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await confidentialClientApplication.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
                        }
                        break;
                    }
                case ClientContextType.DeviceLogin:
                    {
                        var accounts = await publicClientApplication.GetAccountsAsync();
                        try
                        {
                            authResult = await publicClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await publicClientApplication.AcquireTokenWithDeviceCode(scopes, deviceCodeCallback).ExecuteAsync(cancellationToken);
                        }
                        break;
                    }
                case ClientContextType.AzureOnBehalfOf:
                    {
                        var accounts = await confidentialClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await confidentialClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await confidentialClientApplication.AcquireTokenOnBehalfOf(scopes, assertion).ExecuteAsync(cancellationToken);
                        }
                        break;
                    }
                case ClientContextType.SharePointACSAppOnly:
                    {
                        return appOnlyAccessToken;
                    }
            }
            if (authResult?.AccessToken != null)
            {
                return authResult.AccessToken;
            }
            return null;
        }

        /// <summary>
        /// Returns a CSOM ClientContext which has been set up for Azure AD OAuth authentication
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <returns></returns>
        public ClientContext GetContext(string siteUrl)
        {
            return GetContextAsync(siteUrl, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a CSOM ClientContext which has been set up for Azure AD OAuth authentication
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the request</param>
        /// <returns></returns>
        public ClientContext GetContext(string siteUrl, CancellationToken cancellationToken)
        {
            return GetContextAsync(siteUrl, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a CSOM ClientContext which has been set up for Azure AD OAuth authentication
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <returns></returns>
        public async Task<ClientContext> GetContextAsync(string siteUrl)
        {
            return await GetContextAsync(siteUrl, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a CSOM ClientContext which has been set up for Azure AD OAuth authentication
        /// </summary>
        /// <param name="siteUrl"></param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the request</param>
        /// <returns></returns>
        public async Task<ClientContext> GetContextAsync(string siteUrl, CancellationToken cancellationToken)
        {
            var uri = new Uri(siteUrl);

            var scopes = new[] { $"{uri.Scheme}://{uri.Authority}/.default" };

            AuthenticationResult authResult;

            switch (authenticationType)
            {
                case ClientContextType.AzureADCredentials:
                    {
                        var accounts = await publicClientApplication.GetAccountsAsync();
                        try
                        {
                            authResult = await publicClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await publicClientApplication.AcquireTokenByUsernamePassword(scopes, username, password).ExecuteAsync(cancellationToken);
                        }
                        if (authResult.AccessToken != null)
                        {
                            return BuildClientContext(publicClientApplication, siteUrl, scopes, authenticationType);
                        }
                        break;
                    }
                case ClientContextType.AzureADInteractive:
                    {
                        var accounts = await publicClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await publicClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            var builder = publicClientApplication.AcquireTokenInteractive(scopes);
                            if (customWebUi != null)
                            {
                                builder = builder.WithCustomWebUi(customWebUi);
                            }
                            authResult = await builder.ExecuteAsync(cancellationToken);
                        }
                        if (authResult.AccessToken != null)
                        {
                            return BuildClientContext(publicClientApplication, siteUrl, scopes, authenticationType);
                        }
                        break;
                    }
                case ClientContextType.AzureADCertificate:
                    {
                        var accounts = await confidentialClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await confidentialClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await confidentialClientApplication.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
                        }
                        if (authResult.AccessToken != null)
                        {
                            return BuildClientContext(confidentialClientApplication, siteUrl, scopes, authenticationType);
                        }
                        break;
                    }
                case ClientContextType.AzureOnBehalfOf:
                    {
                        var accounts = await confidentialClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await confidentialClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await confidentialClientApplication.AcquireTokenOnBehalfOf(scopes, assertion).ExecuteAsync(cancellationToken);
                        }
                        if (authResult.AccessToken != null)
                        {
                            return BuildClientContext(confidentialClientApplication, siteUrl, scopes, authenticationType);
                        }
                        break;
                    }
                case ClientContextType.DeviceLogin:
                    {
                        var accounts = await publicClientApplication.GetAccountsAsync();

                        try
                        {
                            authResult = await publicClientApplication.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync(cancellationToken);
                        }
                        catch
                        {
                            authResult = await publicClientApplication.AcquireTokenWithDeviceCode(scopes, deviceCodeCallback).ExecuteAsync(cancellationToken);
                        }
                        if (authResult.AccessToken != null)
                        {
                            return BuildClientContext(publicClientApplication, siteUrl, scopes, authenticationType);
                        }
                        break;
                    }
            }
            return null;
        }


        private ClientContext BuildClientContext(IClientApplicationBase application, string siteUrl, string[] scopes, ClientContextType contextType)
        {
            var clientContext = new ClientContext(siteUrl)
            {
                DisableReturnValueCache = true
            };

            clientContext.ExecutingWebRequest += (sender, args) =>
            {
                AuthenticationResult ar = null;

                var accounts = application.GetAccountsAsync().GetAwaiter().GetResult();
                if (accounts.Any())
                {
                    ar = application.AcquireTokenSilent(scopes, accounts.First()).ExecuteAsync().GetAwaiter().GetResult();
                }
                else
                {
                    switch (contextType)
                    {
                        case ClientContextType.AzureADCertificate:
                            {
                                ar = ((IConfidentialClientApplication)application).AcquireTokenForClient(scopes).ExecuteAsync().GetAwaiter().GetResult();
                                break;
                            }
                        case ClientContextType.AzureADCredentials:
                            {
                                ar = ((IPublicClientApplication)application).AcquireTokenByUsernamePassword(scopes, username, password).ExecuteAsync().GetAwaiter().GetResult();
                                break;
                            }
                        case ClientContextType.AzureADInteractive:
                            {
                                var builder = ((IPublicClientApplication)application).AcquireTokenInteractive(scopes);
                                if (customWebUi != null)
                                {
                                    builder = builder.WithCustomWebUi(customWebUi);
                                }
                                ar = builder.ExecuteAsync().GetAwaiter().GetResult();
                                break;
                            }
                        case ClientContextType.AzureOnBehalfOf:
                            {
                                ar = ((IConfidentialClientApplication)application).AcquireTokenOnBehalfOf(scopes, assertion).ExecuteAsync().GetAwaiter().GetResult();
                                break;
                            }
                        case ClientContextType.DeviceLogin:
                            {
                                ar = ((IPublicClientApplication)application).AcquireTokenWithDeviceCode(scopes, deviceCodeCallback).ExecuteAsync().GetAwaiter().GetResult();
                                break;
                            }

                    }

                }
                if (ar != null && ar.AccessToken != null)
                {
                    args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + ar.AccessToken;
                }
            };

            ClientContextSettings clientContextSettings = new ClientContextSettings()
            {
                Type = contextType,
                SiteUrl = siteUrl,
                AuthenticationManager = this,
            };

            clientContext.AddContextSettings(clientContextSettings);

            return clientContext;
        }

        private static string GetKnownClientId(KnownClientId id)
        {
            switch (id)
            {
                case KnownClientId.PnPManagementShell:
                    {
                        return CLIENTID_PNPMANAGEMENTSHELL;
                    }
                case KnownClientId.SPOManagementShell:
                    {
                        return CLIENTID_SPOMANAGEMENTSHELL;
                    }
                default:
                    {
                        return CLIENTID_SPOMANAGEMENTSHELL;
                    }
            }
        }

        /// <summary>
        /// Returns an app only ClientContext object
        /// </summary>
        /// <param name="siteUrl">Site for which the ClientContext object will be instantiated</param>
        /// <param name="appId">Application ID which is requesting the ClientContext object</param>
        /// <param name="appSecret">Application secret of the Application which is requesting the ClientContext object</param>
        /// <returns>ClientContext to be used by CSOM code</returns>
        public ClientContext GetACSAppOnlyContext(string siteUrl, string appId, string appSecret)
        {
            return GetACSAppOnlyContext(siteUrl, Utilities.TokenHelper.GetRealmFromTargetUrl(new Uri(siteUrl)), appId, appSecret);
        }

        /// <summary>
        /// Returns an app only ClientContext object
        /// </summary>
        /// <param name="siteUrl">Site for which the ClientContext object will be instantiated</param>
        /// <param name="appId">Application ID which is requesting the ClientContext object</param>
        /// <param name="appSecret">Application secret of the Application which is requesting the ClientContext object</param>
        /// <param name="environment">SharePoint environment being used</param>
        /// <returns>ClientContext to be used by CSOM code</returns>
        public ClientContext GetACSAppOnlyContext(string siteUrl, string appId, string appSecret, AzureEnvironment environment = AzureEnvironment.Production)
        {
            return GetACSAppOnlyContext(siteUrl, Utilities.TokenHelper.GetRealmFromTargetUrl(new Uri(siteUrl)), appId, appSecret, GetACSEndPoint(environment), GetACSEndPointPrefix(environment));
        }

        /// <summary>
        /// Returns an app only ClientContext object
        /// </summary>
        /// <param name="siteUrl">Site for which the ClientContext object will be instantiated</param>
        /// <param name="realm">Realm of the environment (tenant) that requests the ClientContext object</param>
        /// <param name="appId">Application ID which is requesting the ClientContext object</param>
        /// <param name="appSecret">Application secret of the Application which is requesting the ClientContext object</param>
        /// <param name="acsHostUrl">Azure ACS host, defaults to accesscontrol.windows.net but internal pre-production environments use other hosts</param>
        /// <param name="globalEndPointPrefix">Azure ACS endpoint prefix, defaults to accounts but internal pre-production environments use other prefixes</param>
        /// <returns>ClientContext to be used by CSOM code</returns>
        public ClientContext GetACSAppOnlyContext(string siteUrl, string realm, string appId, string appSecret, string acsHostUrl = "accesscontrol.windows.net", string globalEndPointPrefix = "accounts")
        {
            ACSEnsureToken(siteUrl, realm, appId, appSecret, acsHostUrl, globalEndPointPrefix);
            ClientContext clientContext = Utilities.TokenHelper.GetClientContextWithAccessToken(siteUrl, appOnlyAccessToken);
            clientContext.DisableReturnValueCache = true;

            ClientContextSettings clientContextSettings = new ClientContextSettings()
            {
                Type = ClientContextType.SharePointACSAppOnly,
                SiteUrl = siteUrl,
                AuthenticationManager = this,
                Realm = realm,
                ClientId = appId,
                ClientSecret = appSecret,
                AcsHostUrl = acsHostUrl,
                GlobalEndPointPrefix = globalEndPointPrefix
            };

            clientContext.AddContextSettings(clientContextSettings);

            return clientContext;
        }

        /// <summary>
        /// Gets the Azure ASC login end point for the given environment
        /// </summary>
        /// <param name="environment">Environment to get the login information for</param>
        /// <returns>Azure ASC login endpoint</returns>
        public static string GetACSEndPoint(AzureEnvironment environment)
        {
            switch (environment)
            {
                case AzureEnvironment.Production:
                    {
                        return "accesscontrol.windows.net";
                    }
                case AzureEnvironment.Germany:
                    {
                        return "microsoftonline.de";
                    }
                case AzureEnvironment.China:
                    {
                        return "accesscontrol.chinacloudapi.cn";
                    }
                case AzureEnvironment.USGovernment:
                    {
                        return "microsoftonline.us";
                    }
                case AzureEnvironment.PPE:
                    {
                        return "windows-ppe.net";
                    }
                default:
                    {
                        return "accesscontrol.windows.net";
                    }
            }
        }

        /// <summary>
        /// Gets the Azure ACS login end point prefix for the given environment
        /// </summary>
        /// <param name="environment">Environment to get the login information for</param>
        /// <returns>Azure ACS login endpoint prefix</returns>
        public static string GetACSEndPointPrefix(AzureEnvironment environment)
        {
            switch (environment)
            {
                case AzureEnvironment.Production:
                    {
                        return "accounts";
                    }
                case AzureEnvironment.Germany:
                    {
                        return "login";
                    }
                case AzureEnvironment.China:
                    {
                        return "accounts";
                    }
                case AzureEnvironment.USGovernment:
                    {
                        return "login";
                    }
                case AzureEnvironment.PPE:
                    {
                        return "login";
                    }
                default:
                    {
                        return "accounts";
                    }
            }
        }

        /// <summary>
        /// Ensure that AppAccessToken is filled with a valid string representation of the OAuth AccessToken. This method will launch handle with token cleanup after the token expires
        /// </summary>
        /// <param name="siteUrl">Site for which the ClientContext object will be instantiated</param>
        /// <param name="realm">Realm of the environment (tenant) that requests the ClientContext object</param>
        /// <param name="appId">Application ID which is requesting the ClientContext object</param>
        /// <param name="appSecret">Application secret of the Application which is requesting the ClientContext object</param>
        /// <param name="acsHostUrl">Azure ACS host, defaults to accesscontrol.windows.net but internal pre-production environments use other hosts</param>
        /// <param name="globalEndPointPrefix">Azure ACS endpoint prefix, defaults to accounts but internal pre-production environments use other prefixes</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1303:Do not pass literals as localized parameters", MessageId = "OfficeDevPnP.Core.Diagnostics.Log.Debug(System.String,System.String,System.Object[])")]
        private void ACSEnsureToken(string siteUrl, string realm, string appId, string appSecret, string acsHostUrl, string globalEndPointPrefix)
        {
            if (appOnlyAccessToken == null)
            {
                lock (tokenLock)
                {
                    Log.Debug(Constants.LOGGING_SOURCE, "AuthenticationManager:EnsureToken(siteUrl:{0},realm:{1},appId:{2},appSecret:PRIVATE)", siteUrl, realm, appId);
                    if (appOnlyAccessToken == null)
                    {
                        Utilities.TokenHelper.Realm = realm;
                        Utilities.TokenHelper.ServiceNamespace = realm;
                        Utilities.TokenHelper.ClientId = appId;
                        Utilities.TokenHelper.ClientSecret = appSecret;

                        if (!String.IsNullOrEmpty(acsHostUrl))
                        {
                            Utilities.TokenHelper.AcsHostUrl = acsHostUrl;
                        }

                        if (globalEndPointPrefix != null)
                        {
                            Utilities.TokenHelper.GlobalEndPointPrefix = globalEndPointPrefix;
                        }

                        var response = Utilities.TokenHelper.GetAppOnlyAccessToken(SHAREPOINT_PRINCIPAL, new Uri(siteUrl).Authority, realm);
                        string token = response.AccessToken;

                        try
                        {
                            Log.Debug(Constants.LOGGING_SOURCE, "Lease expiration date: {0}", response.ExpiresOn);
                            var lease = GetAccessTokenLease(response.ExpiresOn);
                            lease = TimeSpan.FromSeconds(lease.TotalSeconds - TimeSpan.FromMinutes(5).TotalSeconds > 0 ? lease.TotalSeconds - TimeSpan.FromMinutes(5).TotalSeconds : lease.TotalSeconds);



                            appOnlyACSAccessTokenResetEvent = new AutoResetEvent(false);

                            ACSAppOnlyAccessTokenWaitInfo wi = new ACSAppOnlyAccessTokenWaitInfo();

                            wi.Handle = ThreadPool.RegisterWaitForSingleObject(appOnlyACSAccessTokenResetEvent,
                                                                               new WaitOrTimerCallback(ACSAppOnlyAccessTokenWaitProc),
                                                                               wi,
                                                                               (uint)lease.TotalMilliseconds,
                                                                               true);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(Constants.LOGGING_SOURCE, CoreResources.AuthenticationManger_ProblemDeterminingTokenLease, ex);
                            appOnlyAccessToken = null;
                        }

                        appOnlyAccessToken = token;
                    }
                }
            }
        }

        internal class ACSAppOnlyAccessTokenWaitInfo
        {
            public RegisteredWaitHandle Handle;
        }

        internal void ACSAppOnlyAccessTokenWaitProc(object state, bool timedOut)
        {
            if (!timedOut)
            {
                ACSAppOnlyAccessTokenWaitInfo wi = (ACSAppOnlyAccessTokenWaitInfo)state;
                if (wi.Handle != null)
                {
                    wi.Handle.Unregister(null);
                }
            }
            else
            {
                appOnlyAccessToken = null;
            }
        }

        /// <summary>
        /// Get the access token lease time span.
        /// </summary>
        /// <param name="expiresOn">The ExpiresOn time of the current access token</param>
        /// <returns>Returns a TimeSpan represents the time interval within which the current access token is valid thru.</returns>
        private TimeSpan GetAccessTokenLease(DateTime expiresOn)
        {
            DateTime now = DateTime.UtcNow;
            DateTime expires = expiresOn.Kind == DateTimeKind.Utc ?
                expiresOn : TimeZoneInfo.ConvertTimeToUtc(expiresOn);
            TimeSpan lease = expires - now;
            return lease;
        }

        /// <summary>
        /// Returns a SharePoint ClientContext using Azure Active Directory authentication. This requires that you have a Azure AD Web Application registered. The user will not be prompted for authentication, the current user's authentication context will be used by leveraging ADAL.
        /// </summary>
        /// <param name="siteUrl">Site for which the ClientContext object will be instantiated</param>
        /// <param name="accessTokenGetter">The AccessToken getter method to use</param>
        /// <returns>Client context object</returns>
        public ClientContext GetAccessTokenContext(string siteUrl, Func<string, string> accessTokenGetter)
        {
            var clientContext = new ClientContext(siteUrl)
            {
                DisableReturnValueCache = true
            };

            clientContext.ExecutingWebRequest += (sender, args) =>
            {
                Uri resourceUri = new Uri(siteUrl);
                resourceUri = new Uri(resourceUri.Scheme + "://" + resourceUri.Host + "/");

                string accessToken = accessTokenGetter(resourceUri.ToString());
                args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + accessToken;
            };

            return clientContext;
        }

        /// <summary>
        /// Returns a SharePoint ClientContext using Azure Active Directory authentication. This requires that you have a Azure AD Web Application registered. The user will not be prompted for authentication, the current user's authentication context will be used by leveraging an explicit OAuth 2.0 Access Token value.
        /// </summary>
        /// <param name="siteUrl">Site for which the ClientContext object will be instantiated</param>
        /// <param name="accessToken">An explicit value for the AccessToken</param>
        /// <returns>Client context object</returns>
        public ClientContext GetAccessTokenContext(string siteUrl, string accessToken)
        {
            var clientContext = new ClientContext(siteUrl)
            {
                DisableReturnValueCache = true
            };

            clientContext.ExecutingWebRequest += (sender, args) =>
            {
                args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + accessToken;
            };

            return clientContext;
        }

        /// <summary>
        /// Gets the Azure AD login end point for the given environment
        /// </summary>
        /// <param name="environment">Environment to get the login information for</param>
        /// <returns>Azure AD login endpoint</returns>
        public string GetAzureADLoginEndPoint(AzureEnvironment environment)
        {
            switch (environment)
            {
                case AzureEnvironment.Production:
                    {
                        return "https://login.microsoftonline.com";
                    }
                case AzureEnvironment.Germany:
                    {
                        return "https://login.microsoftonline.de";
                    }
                case AzureEnvironment.China:
                    {
                        return "https://login.chinacloudapi.cn";
                    }
                case AzureEnvironment.USGovernment:
                    {
                        return "https://login.microsoftonline.us";
                    }
                case AzureEnvironment.PPE:
                    {
                        return "https://login.windows-ppe.net";
                    }
                default:
                    {
                        return "https://login.microsoftonline.com";
                    }
            }
        }

        /// <summary>
        /// Returns a domain suffix (com, us, de, cn) for an Azure Environment
        /// </summary>
        /// <param name="environment"></param>
        /// <returns></returns>
        public static string GetSharePointDomainSuffix(AzureEnvironment environment)
        {
            if (environment == AzureEnvironment.Production)
            {
                return "com";
            }
            else if (environment == AzureEnvironment.USGovernment)
            {
                return "us";
            }
            else if (environment == AzureEnvironment.Germany)
            {
                return "de";
            }
            else if (environment == AzureEnvironment.China)
            {
                return "cn";
            }

            return "com";
        }

        /// <summary>
        /// called when disposing the object
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (appOnlyACSAccessTokenResetEvent != null)
                    {
                        appOnlyACSAccessTokenResetEvent.Set();
                        appOnlyACSAccessTokenResetEvent?.Dispose();
                    }
                }

                disposedValue = true;
            }
        }

        /// <summary>
        /// Dispose the object
        /// </summary>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
