using System.Security.Cryptography.X509Certificates;

var config = ConfigLoader.Load("config.yaml");
var httpServer = new WebServer(
    config.Port,
    config.ContentPath,
    redirectToHttps: config.Https.Enabled,
    httpsPort: config.Https.Port
);

if (!config.Https.Enabled)
{
    await httpServer.RunAsync();
    return;
}

var cert = X509CertificateLoader.LoadPkcs12FromFile(
    config.Https.CertificatePath,
    config.Https.CertificatePassword
);
var httpsServer = new HttpsWebServer(config.Https.Port, config.ContentPath, cert);

await Task.WhenAll(
    httpServer.RunAsync(),
    httpsServer.RunAsync()
);
