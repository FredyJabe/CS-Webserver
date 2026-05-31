using System.Security.Cryptography.X509Certificates;

try
{
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

    var cert = LoadCertificate(config);
    var httpsServer = new HttpsWebServer(config.Https.Port, config.ContentPath, cert);

    await Task.WhenAll(
        httpServer.RunAsync(),
        httpsServer.RunAsync()
    );
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static X509Certificate2 LoadCertificate(ServerConfig config)
{
    var pem = config.Https.Certificate.Pem;
    if (pem.UsePem)
    {
        return string.IsNullOrWhiteSpace(pem.KeyPassword)
            ? X509Certificate2.CreateFromPemFile(pem.CertPath, pem.KeyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(
                pem.CertPath,
                pem.KeyPassword,
                pem.KeyPath
            );
    }

    return X509CertificateLoader.LoadPkcs12FromFile(
        config.Https.Certificate.Path,
        config.Https.Certificate.Password
    );
}
