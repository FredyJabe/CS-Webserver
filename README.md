# C-Webserver

[![Last Build](https://img.shields.io/github/actions/workflow/status/FredyJabe/C-Webserver/publish-single-file.yml?label=Last%20Build)](https://github.com/FredyJabe/C-Webserver/actions/workflows/publish-single-file.yml)
[![GitHub Stars](https://img.shields.io/github/stars/FredyJabe/C-Webserver?style=flat)](https://github.com/FredyJabe/C-Webserver/stargazers)
[![Downloads](https://img.shields.io/github/downloads/FredyJabe/C-Webserver/total?style=flat)](https://github.com/FredyJabe/C-Webserver/releases)

A lightweight C# web server that serves static files, markdown pages, and server-rendered `<cs>...</cs>` blocks.

## What It Can Do

- Serve HTTP content from a configurable content directory
- Optionally serve HTTPS using a local `.pfx` certificate
- Auto-redirect all HTTP traffic to HTTPS when HTTPS is enabled
- Render markdown (`.md`) as HTML
- Execute server-side C# in `<cs>...</cs>` blocks
- Allow `<cs>` scripts in HTML pages to access DOM nodes by id (`GetById(...)`)
- Auto-create `config.yaml` if it is missing
- Expose `/health` endpoint (`200 healthy`)

## Project Structure

- `Program.cs`: app startup and service wiring
- `ConfigLoader.cs`: `config.yaml` loading + defaults + HTTPS validation/fallback
- `WebServer.cs`: HTTP listener
- `HttpsWebServer.cs`: HTTPS listener (TLS 1.2/1.3)
- `ContentService.cs`: shared content serving logic for HTTP + HTTPS
- `CsTagsParser.cs`: `<cs>` parser + script execution globals
- `MarkdownParser.cs`: markdown to HTML renderer

## Configuration

Configuration file: `config.yaml`

```yaml
port: 8080
contentPath: ./content
https:
  enabled: false
  port: 8443
  certificate:
    path: ./certs/server.pfx
    pass: change-me
    pem:
      usePem: false
      certPath: ./certs/server.crt
      keyPath: ./certs/server.key
      keyPass: ""
```

### Config fields

- `port`: HTTP port
- `contentPath`: directory to serve files from
- `https.enabled`: enables HTTPS listener + HTTP→HTTPS redirects
- `https.port`: HTTPS port
- `https.certificate.path`: path to `.pfx` certificate file
- `https.certificate.pass`: `.pfx` certificate password
- `https.certificate.pem.usePem`: when `true`, load certificate from PEM files instead of `.pfx`
- `https.certificate.pem.certPath`: PEM certificate path
- `https.certificate.pem.keyPath`: PEM private key path
- `https.certificate.pem.keyPass`: encrypted PEM key password (empty if key is unencrypted)

### Missing/invalid HTTPS cert behavior

If `https.enabled: true` but certificate config is invalid:

- Server logs a warning
- HTTPS is disabled automatically
- HTTP continues running (no crash)

## Local HTTPS Certificate (Dev)

Generate a local `.pfx` cert for this project:

```bash
mkdir -p certs
dotnet dev-certs https -ep ./certs/server.pfx -p change-me
```

Optional (trust dev cert on your machine):

```bash
dotnet dev-certs https --trust
```

Use matching config values:

```yaml
https:
  enabled: true
  port: 8443
  certificate:
    path: ./certs/server.pfx
    pass: change-me
    pem:
      usePem: false
      certPath: ./certs/server.crt
      keyPath: ./certs/server.key
      keyPass: ""
```

## Running

```bash
dotnet run
```

Then browse:

- HTTP: `http://localhost:<port>`
- HTTPS: `https://localhost:<httpsPort>` (if enabled)

Health check:

- `http://localhost:<port>/health`

## Content Behavior

### Static files

Serves files directly from `contentPath` with content-type mapping for common extensions.

### Markdown files

- `.md` files are converted to HTML
- Supports headings, lists, code fences, tables, blockquotes, inline code, emphasis, and horizontal rules

### `<cs>` server-side rendering

`<cs>` blocks are executed on the server and replaced with their output.
The original `<cs>` tags/code are never sent to clients.
NuGet packages can be pulled in dynamically from `<cs>` blocks with either
`#r "nuget: PackageId, Version"` or `#:package PackageId@Version`.
The parser resolves packages at runtime through the NuGet V3 feed, downloads
the package graph it needs, extracts it into a local staging cache, and adds
the managed assemblies before compiling the script. Use `@*` or omit the
version to ask for the latest available package version.

#### In markdown/text

```md
Current time: <cs>Now.ToString("yyyy-MM-dd HH:mm:ss")</cs>
```

#### In HTML with DOM access

```html
<div id="time">Loading...</div>
<cs>
var el = GetById("time");
if (el is not null)
{
    el.InnerHtml = $"Server time: {Now:yyyy-MM-dd HH:mm:ss}";
}
"";
</cs>
```

### Script globals available in `<cs>`

- `Now`
- `UtcNow`
- `FilePath`
- `RequestPath`
- `Document` (HTML only)
- `GetById(string id)` (HTML only)

## Notes

- Sending HTTPS traffic to the HTTP port is safely ignored as invalid HTTP input.
- HTTP redirect uses `308 Permanent Redirect` when HTTPS is enabled.
