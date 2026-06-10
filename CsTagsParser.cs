using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Collections.Concurrent;
using System.Reflection;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using HtmlAgilityPack;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis;

public static class CsTagsParser
{
    private static readonly Regex NugetDirectiveLineRegex = new(
        @"^\s*(?:#r\s+""nuget:\s*(?<package>[^,\s""]+)\s*,\s*(?<version>[^""]+)""|#:\s*package\s+(?<package2>[^\s@]+)(?:@(?<version2>[^\s#]+))?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly string[] PreferredFrameworkFolders =
    [
        "net10.0",
        "net9.0",
        "net8.0",
        "net7.0",
        "net6.0",
        "net5.0",
        "netcoreapp3.1",
        "netcoreapp3.0",
        "netstandard2.1",
        "netstandard2.0",
        "net472",
        "net471",
        "net47.2",
        "net47",
        "net462",
        "net461",
        "net46",
        "net45",
        "net40",
        "net35"
    ];

    private static readonly ConcurrentDictionary<string, string> NativeLibraryPathMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object NativeLibraryResolverLock = new();
    private static bool nativeLibraryResolverRegistered;
    private static readonly SourceRepository NuGetRepository = CreateNuGetRepository();
    private static readonly object NuGetResolutionLock = new();
    private static readonly Dictionary<string, PackageIdentity> PackageIdentityCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ManagedAssemblyResolverRegistration = new(StringComparer.OrdinalIgnoreCase);

    private static SourceRepository CreateNuGetRepository()
    {
        var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
        return new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
    }

    public static async Task<string> RenderInTextAsync(
        string content,
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response)
    {
        var matches = Regex.Matches(content, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return content;
        }

        var rendered = new StringBuilder(content.Length);
        var cursor = 0;
        var scriptOptions = BuildScriptOptions();
        var context = new CsScriptExecutionContext(scriptOptions);
        var globals = new CsScriptGlobals(filePath, contentRoot, request, response, context);

        foreach (Match match in matches)
        {
            rendered.Append(content, cursor, match.Index - cursor);
            try
            {
                var scriptBody = match.Groups[1].Value;
                if (TryGetHiddenIncludePath(scriptBody, out var includePath))
                {
                    await ExecuteHiddenIncludeAsync(includePath, filePath, contentRoot, request, response, context, globals);
                    rendered.Append(string.Empty);
                    cursor = match.Index + match.Length;
                    continue;
                }

                await ExecuteScriptAsync(scriptBody, context, globals);
                rendered.Append(context.State?.ReturnValue?.ToString() ?? string.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"<cs> parser error in '{filePath}': {ex.Message}");
                rendered.Append(string.Empty);
            }

            cursor = match.Index + match.Length;
        }

        rendered.Append(content, cursor, content.Length - cursor);
        return rendered.ToString();
    }

    public static async Task<string> RenderInHtmlAsync(
        string html,
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response)
    {
        var csMatches = Regex.Matches(html, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        if (csMatches.Count == 0)
        {
            return html;
        }

        var scripts = new List<string>(csMatches.Count);
        var htmlWithPlaceholders = Regex.Replace(
            html,
            "<cs>([\\s\\S]*?)</cs>",
            match =>
            {
                var scriptBody = match.Groups[1].Value;
                scripts.Add(scriptBody);
                var marker = $"CSBLOCK_{scripts.Count - 1}";
                return $"<!--{marker}-->";
            },
            RegexOptions.IgnoreCase);

        var document = new HtmlDocument();
        document.LoadHtml(htmlWithPlaceholders);

        var scriptOptions = BuildScriptOptions();
        var context = new CsScriptExecutionContext(scriptOptions);
        var globals = new CsScriptGlobals(filePath, contentRoot, request, response, context, document);

        for (var i = 0; i < scripts.Count; i++)
        {
            var marker = document.DocumentNode
                .DescendantsAndSelf()
                .FirstOrDefault(n =>
                    n.NodeType == HtmlNodeType.Comment &&
                    n.InnerHtml.StartsWith($"CSBLOCK_{i}", StringComparison.Ordinal));

            try
            {
                if (TryGetHiddenIncludePath(scripts[i], out var includePath))
                {
                    await ExecuteHiddenIncludeAsync(includePath, filePath, contentRoot, request, response, context, globals);
                    marker?.Remove();
                    continue;
                }

                await ExecuteScriptAsync(scripts[i], context, globals);
                if (marker is null)
                {
                    continue;
                }

                var replacement = context.State?.ReturnValue?.ToString() ?? string.Empty;
                if (replacement.Length == 0)
                {
                    marker.Remove();
                    continue;
                }

                var fragment = HtmlNode.CreateNode($"<span>{replacement}</span>");
                var inserted = fragment.ChildNodes.ToList();
                foreach (var child in inserted)
                {
                    marker.ParentNode?.InsertBefore(child, marker);
                }
                marker.Remove();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"<cs> parser error in '{filePath}': {ex.Message}");
                marker?.Remove();
            }
        }

        var renderedHtml = document.DocumentNode.OuterHtml;
        renderedHtml = Regex.Replace(
            renderedHtml,
            "<!--\\s*CSBLOCK_\\d+\\s*-->",
            string.Empty,
            RegexOptions.IgnoreCase);
        return renderedHtml;
    }

    private static ScriptOptions BuildScriptOptions()
    {
        return BuildBaseScriptOptions();
    }

    private static ScriptOptions BuildBaseScriptOptions()
    {
        return ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(HtmlDocument).Assembly
            )
            .AddImports(
                "System",
                "System.IO",
                "System.Linq",
                "System.Collections.Generic",
                "HtmlAgilityPack"
            );
    }

    internal static async Task<string> PrepareScriptBodyAsync(string scriptBody, CsScriptExecutionContext context)
    {
        var matches = NugetDirectiveLineRegex.Matches(scriptBody);
        if (matches.Count == 0)
        {
            return scriptBody;
        }

        var rendered = new StringBuilder(scriptBody.Length);
        var cursor = 0;

        foreach (Match match in matches)
        {
            rendered.Append(scriptBody, cursor, match.Index - cursor);
            cursor = match.Index + match.Length;

            if (!await TryResolveNugetDirectiveAsync(match, context))
            {
                rendered.Append(match.Value);
            }
        }

        rendered.Append(scriptBody, cursor, scriptBody.Length - cursor);
        return rendered.ToString();
    }

    internal static async Task ExecuteScriptAsync(
        string scriptBody,
        CsScriptExecutionContext context,
        CsScriptGlobals globals)
    {
        var preparedScriptBody = await PrepareScriptBodyAsync(scriptBody, context);
        context.State = context.State is null
            ? await CSharpScript.RunAsync<object?>(preparedScriptBody, context.ScriptOptions, globals: globals)
            : await context.State.ContinueWithAsync<object?>(preparedScriptBody, context.ScriptOptions);
        if (globals.ExecutionContext.State is not null)
        {
            context.State = globals.ExecutionContext.State;
        }
    }

    private static async Task<bool> TryResolveNugetDirectiveAsync(Match match, CsScriptExecutionContext context)
    {
        var packageId = match.Groups["package"].Success
            ? match.Groups["package"].Value.Trim()
            : match.Groups["package2"].Value.Trim();
        var versionText = match.Groups["version"].Success
            ? match.Groups["version"].Value.Trim()
            : match.Groups["version2"].Value.Trim();
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(versionText))
        {
            versionText = "*";
        }

        var identity = await ResolveNuGetPackageIdentityAsync(packageId, versionText);
        if (identity is null)
        {
            Console.WriteLine($"<cs> could not resolve NuGet package '{packageId}', version '{versionText}'.");
            return false;
        }

        var packageFolder = await EnsureNuGetPackageAvailableAsync(identity);
        if (packageFolder is null)
        {
            Console.WriteLine($"<cs> could not download NuGet package '{identity.Id}', version '{identity.Version}'.");
            return false;
        }

        EnsureNativeLibraryResolverRegistered();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencePaths = await ResolveNuGetAssemblyPathsAsync(identity, packageFolder, context, visited);
        if (referencePaths.Count == 0)
        {
            Console.WriteLine($"<cs> could not resolve NuGet package '{identity.Id}', version '{identity.Version}'.");
            return false;
        }

        var newReferences = new List<MetadataReference>(referencePaths.Count);
        foreach (var referencePath in referencePaths)
        {
            RegisterManagedAssemblyResolver(referencePath);
            if (context.LoadedReferencePaths.Add(referencePath))
            {
                newReferences.Add(MetadataReference.CreateFromFile(referencePath));
            }
        }

        if (newReferences.Count > 0)
        {
            context.ScriptOptions = context.ScriptOptions.AddReferences(newReferences);
        }

        return true;
    }

    private static async Task<PackageIdentity?> ResolveNuGetPackageIdentityAsync(string packageId, string versionText)
    {
        var cacheKey = $"{packageId.ToLowerInvariant()}|{versionText.Trim()}";
        lock (NuGetResolutionLock)
        {
            if (PackageIdentityCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var identity = await ResolveNuGetPackageIdentityCoreAsync(packageId, versionText);
        if (identity is not null)
        {
            lock (NuGetResolutionLock)
            {
                PackageIdentityCache[cacheKey] = identity;
            }
        }

        return identity;
    }

    private static async Task<PackageIdentity?> ResolveNuGetPackageIdentityCoreAsync(string packageId, string versionText)
    {
        VersionRange? versionRange = null;
        if (!string.IsNullOrWhiteSpace(versionText) && !string.Equals(versionText, "*", StringComparison.Ordinal))
        {
            if (!VersionRange.TryParse(versionText, out versionRange))
            {
                versionRange = VersionRange.Parse(NormalizeNuGetVersionText(versionText));
            }
        }

        using var sourceCacheContext = new SourceCacheContext();
        var metadataResource = await NuGetRepository.GetResourceAsync<PackageMetadataResource>();
        var metadata = await metadataResource.GetMetadataAsync(
            packageId,
            includePrerelease: true,
            includeUnlisted: false,
            sourceCacheContext,
            NullLogger.Instance,
            CancellationToken.None);

        var candidates = metadata
            .Select(item => item.Identity)
            .Where(identity => versionRange is null || versionRange.Satisfies(identity.Version))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(identity => identity.Version)
            .First();
    }

    private static async Task<string?> EnsureNuGetPackageAvailableAsync(PackageIdentity identity)
    {
        var packageFolder = GetNuGetPackageExtractionFolder(identity.Id, identity.Version.ToNormalizedString());
        var packageFilePath = GetNuGetPackageFilePath(packageFolder, identity);

        if (File.Exists(packageFilePath) && Directory.Exists(packageFolder))
        {
            return packageFolder;
        }

        Directory.CreateDirectory(packageFolder);

        var downloadResource = await NuGetRepository.GetResourceAsync<DownloadResource>();
        using var sourceCacheContext = new SourceCacheContext();
        var downloadContext = new PackageDownloadContext(sourceCacheContext);
        using var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
            identity,
            downloadContext,
            packageFolder,
            NullLogger.Instance,
            CancellationToken.None);

        if (downloadResult.Status != DownloadResourceResultStatus.Available)
        {
            return null;
        }

        await using (var packageStream = downloadResult.PackageStream)
        await using (var fileStream = File.Create(packageFilePath))
        {
            await packageStream.CopyToAsync(fileStream);
        }

        ZipFile.ExtractToDirectory(packageFilePath, packageFolder, overwriteFiles: true);
        return packageFolder;
    }

    private static async Task<List<string>> ResolveNuGetAssemblyPathsAsync(
        PackageIdentity identity,
        string packageFolder,
        CsScriptExecutionContext context,
        HashSet<string> visited)
    {
        var packageKey = GetPackageKey(identity, packageFolder);
        if (!visited.Add(packageKey))
        {
            return [];
        }

        foreach (var nativeLibraryPath in FindNativeLibraryPaths(packageFolder))
        {
            RegisterNativeLibraryPath(nativeLibraryPath);
            if (context.LoadedNativeLibraryPaths.Add(nativeLibraryPath))
            {
                try
                {
                    NativeLibrary.Load(nativeLibraryPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"<cs> could not load native library '{nativeLibraryPath}': {ex.Message}");
                }
            }
        }

        var assemblyPaths = new List<string>();
        assemblyPaths.AddRange(FindManagedAssemblyPaths(packageFolder));

        var packageFilePath = GetNuGetPackageFilePath(packageFolder, identity);
        if (!File.Exists(packageFilePath))
        {
            return assemblyPaths;
        }

        using var packageReader = new PackageArchiveReader(packageFilePath);
        var dependencyGroups = await packageReader.GetPackageDependenciesAsync(CancellationToken.None);
        var selectedDependencies = ChooseBestDependencyGroup(dependencyGroups);

        foreach (var dependency in selectedDependencies)
        {
            var dependencyVersionText = dependency.VersionRange is null
                ? "*"
                : dependency.VersionRange.ToNormalizedString();

            var dependencyIdentity = await ResolveNuGetPackageIdentityAsync(dependency.Id, dependencyVersionText);
            if (dependencyIdentity is null)
            {
                continue;
            }

            var dependencyFolder = await EnsureNuGetPackageAvailableAsync(dependencyIdentity);
            if (dependencyFolder is null)
            {
                continue;
            }

            assemblyPaths.AddRange(await ResolveNuGetAssemblyPathsAsync(dependencyIdentity, dependencyFolder, context, visited));
        }

        return assemblyPaths;
    }

    private static IEnumerable<PackageDependency> ChooseBestDependencyGroup(IEnumerable<PackageDependencyGroup> dependencyGroups)
    {
        var groups = dependencyGroups.ToList();
        if (groups.Count == 0)
        {
            return [];
        }

        var reducer = new FrameworkReducer();
        var targetFramework = NuGetFramework.ParseFolder("net10.0");
        var nearest = reducer.GetNearest(targetFramework, groups.Select(group => group.TargetFramework));

        if (nearest is not null)
        {
            var selected = groups.FirstOrDefault(group => group.TargetFramework.Equals(nearest));
            if (selected is not null)
            {
                return selected.Packages;
            }
        }

        var fallback = groups.FirstOrDefault(group => group.TargetFramework.IsAny) ?? groups[0];
        return fallback.Packages;
    }

    private static IEnumerable<string> FindManagedAssemblyPaths(string packageFolder)
    {
        var libFolder = Path.Combine(packageFolder, "lib");
        if (Directory.Exists(libFolder))
        {
            foreach (var assemblyPath in FindBestFrameworkAssemblyPaths(libFolder))
            {
                yield return assemblyPath;
            }
        }

        var refFolder = Path.Combine(packageFolder, "ref");
        if (!Directory.Exists(refFolder))
        {
            yield break;
        }

        foreach (var assemblyPath in FindBestFrameworkAssemblyPaths(refFolder))
        {
            yield return assemblyPath;
        }
    }

    private static IEnumerable<string> FindNativeLibraryPaths(string packageFolder)
    {
        var runtimeFolder = Path.Combine(packageFolder, "runtimes");
        if (Directory.Exists(runtimeFolder))
        {
            var runtimeSpecificFolder = ChooseBestRuntimeFolder(Directory.EnumerateDirectories(runtimeFolder));
            if (runtimeSpecificFolder is not null)
            {
                var nativeFolder = Path.Combine(runtimeSpecificFolder, "native");
                foreach (var nativeLibraryPath in EnumerateNativeLibraryFiles(nativeFolder))
                {
                    yield return nativeLibraryPath;
                }
            }
        }

        var nativeRootFolder = Path.Combine(packageFolder, "native");
        foreach (var nativeLibraryPath in EnumerateNativeLibraryFiles(nativeRootFolder))
        {
            yield return nativeLibraryPath;
        }
    }

    private static IEnumerable<string> EnumerateNativeLibraryFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var nativeLibraryPath in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(nativeLibraryPath).ToLowerInvariant();
            if (extension is ".so" or ".dll" or ".dylib" or ".bundle")
            {
                yield return nativeLibraryPath;
            }
        }
    }

    private static string? ChooseBestRuntimeFolder(IEnumerable<string> folders)
    {
        var folderList = folders.ToList();
        if (folderList.Count == 0)
        {
            return null;
        }

        foreach (var runtimeName in GetPreferredRuntimeNames())
        {
            var match = folderList.FirstOrDefault(folder =>
                string.Equals(Path.GetFileName(folder), runtimeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return folderList[0];
    }

    private static IEnumerable<string> GetPreferredRuntimeNames()
    {
        var osNames = GetCurrentRuntimeOsNames();
        var architectureNames = GetCurrentRuntimeArchitectureNames();

        foreach (var osName in osNames)
        {
            foreach (var architectureName in architectureNames)
            {
                yield return $"{osName}-{architectureName}";
            }

            yield return osName;
        }

        foreach (var architectureName in architectureNames)
        {
            yield return $"unix-{architectureName}";
        }

        yield return "unix";
        yield return "any";
    }

    private static IEnumerable<string> GetCurrentRuntimeOsNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "win";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "osx";
            yield break;
        }

        yield return "linux";
    }

    private static IEnumerable<string> GetCurrentRuntimeArchitectureNames()
    {
        yield return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };
    }

    private static void EnsureNativeLibraryResolverRegistered()
    {
        if (nativeLibraryResolverRegistered)
        {
            return;
        }

        lock (NativeLibraryResolverLock)
        {
            if (nativeLibraryResolverRegistered)
            {
                return;
            }

            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNativeLibrary;
            nativeLibraryResolverRegistered = true;
        }
    }

    private static IntPtr ResolveNativeLibrary(Assembly assembly, string libraryName)
    {
        var lookupNames = GetNativeLibraryLookupNames(libraryName);
        foreach (var lookupName in lookupNames)
        {
            if (!NativeLibraryPathMap.TryGetValue(lookupName, out var nativeLibraryPath))
            {
                continue;
            }

            try
            {
                return NativeLibrary.Load(nativeLibraryPath);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private static void RegisterNativeLibraryPath(string nativeLibraryPath)
    {
        foreach (var lookupName in GetNativeLibraryLookupNames(Path.GetFileName(nativeLibraryPath)))
        {
            NativeLibraryPathMap[lookupName] = nativeLibraryPath;
        }
    }

    private static void RegisterManagedAssemblyResolver(string assemblyPath)
    {
        if (!ManagedAssemblyResolverRegistration.TryAdd(Path.GetFullPath(assemblyPath), 0))
        {
            return;
        }

        try
        {
            var loadedAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.Location, assemblyPath, StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            NativeLibrary.SetDllImportResolver(
                loadedAssembly,
                (libraryName, assembly, searchPath) => ResolveNativeLibrary(assembly, libraryName));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"<cs> could not register DllImport resolver for '{assemblyPath}': {ex.Message}");
        }
    }

    private static IEnumerable<string> GetNativeLibraryLookupNames(string libraryName)
    {
        var fileName = Path.GetFileNameWithoutExtension(libraryName).Trim();
        if (fileName.Length == 0)
        {
            yield break;
        }

        yield return fileName;

        if (fileName.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && fileName.Length > 3)
        {
            yield return fileName[3..];
        }
    }

    private static IEnumerable<string> FindBestFrameworkAssemblyPaths(string parentFolder)
    {
        var frameworkFolder = ChooseBestFrameworkFolder(Directory.EnumerateDirectories(parentFolder));
        if (frameworkFolder is null)
        {
            yield break;
        }

        foreach (var assemblyPath in Directory.EnumerateFiles(frameworkFolder, "*.dll", SearchOption.TopDirectoryOnly))
        {
            yield return assemblyPath;
        }
    }

    private static string? ChooseBestFrameworkFolder(IEnumerable<string> folders)
    {
        var folderLookup = folders
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToDictionary(name => name!, name => name!, StringComparer.OrdinalIgnoreCase);

        foreach (var preferred in PreferredFrameworkFolders)
        {
            if (folderLookup.TryGetValue(preferred, out var folderName))
            {
                return folders.First(folder => Path.GetFileName(folder).Equals(folderName, StringComparison.OrdinalIgnoreCase));
            }
        }

        return folders.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string NormalizeNuGetVersionText(string versionText)
    {
        var trimmed = versionText.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (trimmed[0] == '[' || trimmed[0] == '(')
        {
            var match = Regex.Match(trimmed, @"\d+(?:\.\d+)*(?:[-+][A-Za-z0-9\.-]+)?");
            if (match.Success)
            {
                return match.Value;
            }
        }

        return trimmed.Trim('[', ']', '(', ')');
    }

    private static string GetNuGetPackageExtractionFolder(string packageId, string version)
    {
        var safeId = packageId.Trim().ToLowerInvariant();
        var safeVersion = version.Trim().ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "webserver-nuget", safeId, safeVersion);
    }

    private static string GetNuGetPackageFilePath(string packageFolder, PackageIdentity identity)
    {
        var lowerId = identity.Id.Trim().ToLowerInvariant();
        var lowerVersion = identity.Version.ToNormalizedString().ToLowerInvariant();
        return Path.Combine(packageFolder, $"{lowerId}.{lowerVersion}.nupkg");
    }

    private static string GetPackageKey(PackageIdentity identity, string packageFolder)
    {
        return $"{identity.Id.ToLowerInvariant()}|{identity.Version.ToNormalizedString()}|{Path.GetFullPath(packageFolder)}";
    }

    private static bool TryGetHiddenIncludePath(string scriptBody, out string relativePath)
    {
        var match = Regex.Match(
            scriptBody,
            @"^\s*await\s+IncludeHiddenAsync\(\s*""([^""]+)""\s*\)\s*;\s*$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = match.Groups[1].Value;
        return true;
    }

    private static async Task ExecuteHiddenIncludeAsync(
        string relativePath,
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response,
        CsScriptExecutionContext context,
        CsScriptGlobals globals)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var includePath = Path.GetFullPath(Path.Combine(contentRoot, "hidden", normalized));
        var hiddenRoot = Path.GetFullPath(Path.Combine(contentRoot, "hidden"));

        if (!includePath.StartsWith(hiddenRoot, StringComparison.Ordinal))
        {
            return;
        }

        if (!File.Exists(includePath))
        {
            return;
        }

        var html = await File.ReadAllTextAsync(includePath);

        var matches = Regex.Matches(html, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var scriptBody = match.Groups[1].Value;
            if (TryGetHiddenIncludePath(scriptBody, out var nestedIncludePath))
            {
                await ExecuteHiddenIncludeAsync(nestedIncludePath, filePath, contentRoot, request, response, context, globals);
                continue;
            }

            await ExecuteScriptAsync(scriptBody, context, globals);
        }
    }
}

public sealed class CsScriptExecutionContext(ScriptOptions scriptOptions)
{
    public ScriptOptions ScriptOptions { get; set; } = scriptOptions;
    public HashSet<string> LoadedReferencePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> LoadedNativeLibraryPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ScriptState<object?>? State { get; set; }
}

public sealed class CsScriptGlobals
{
    private readonly HttpRequestContext request;
    private readonly HttpResponseContext response;
    private readonly CsScriptExecutionContext executionContext;

    public CsScriptGlobals(
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response,
        CsScriptExecutionContext executionContext,
        HtmlDocument? document = null)
    {
        FilePath = filePath;
        ContentRoot = contentRoot;
        this.request = request;
        this.response = response;
        this.executionContext = executionContext;
        Document = document;
    }

    public string FilePath { get; }
    public string ContentRoot { get; }
    public string RequestPath => request.RawPath;
    public string RequestMethod => request.Method;
    public string RequestBody => request.Body;
    public IReadOnlyDictionary<string, string> RequestHeaders => request.Headers;
    public IReadOnlyDictionary<string, string> Query => request.Query;
    public IReadOnlyDictionary<string, string> Form => request.Form;
    public IReadOnlyDictionary<string, string> Cookies => request.Cookies;
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
    public HtmlDocument? Document { get; }
    public CsScriptExecutionContext ExecutionContext => executionContext;
    public HtmlNode? GetById(string id) => Document?.GetElementbyId(id);
    public async Task IncludeHiddenAsync(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var includePath = Path.GetFullPath(Path.Combine(ContentRoot, "hidden", normalized));
        var hiddenRoot = Path.GetFullPath(Path.Combine(ContentRoot, "hidden"));

        if (!includePath.StartsWith(hiddenRoot, StringComparison.Ordinal))
        {
            return;
        }

        if (!File.Exists(includePath))
        {
            return;
        }

        var html = await File.ReadAllTextAsync(includePath);
        var matches = Regex.Matches(html, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            await CsTagsParser.ExecuteScriptAsync(match.Groups[1].Value, executionContext, this);
        }
    }
    public string? Header(string name) => request.Headers.TryGetValue(name, out var value) ? value : null;
    public string? Cookie(string name) => request.Cookies.TryGetValue(name, out var value) ? value : null;
    public void SetCookie(
        string name,
        string value,
        string path = "/",
        bool httpOnly = true,
        int? maxAgeSeconds = null,
        bool secure = false,
        string sameSite = "Lax")
    {
        response.SetCookie(name, value, path, httpOnly, maxAgeSeconds, secure, sameSite);
    }
}
