#region Copyright (c) 2016 Atif Aziz. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
#endregion

namespace LinqPadless
{
    #region Imports

    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Xml;
    using System.Xml.Linq;
    using Choices;
    using Mannex.IO;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis;
    using NuGet.Frameworks;
    using NuGet.Versioning;
    using MoreEnumerable = MoreLinq.MoreEnumerable;
    using static MoreLinq.Extensions.TakeUntilExtension;
    using static MoreLinq.Extensions.ToDelimitedStringExtension;
    using static MoreLinq.Extensions.ToDictionaryExtension;
    using Ix = System.Linq.EnumerableEx;
    using OptionSetArgumentParser = System.Func<System.Func<string, Mono.Options.OptionContext, bool>, string, Mono.Options.OptionContext, bool>;
    using Option = Mono.Options.Option;

    #endregion

    static partial class Program
    {
        static int Wain(string[] args)
        {
            var verbose = Ref.Create(false);
            var help = Ref.Create(false);
            var force = false;
            var dontExecute = false;
            var targetFramework = NuGetFramework.Parse(Assembly.GetEntryAssembly().GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName);
            var outDirPath = (string) null;
            var uncached = false;

            var options = new OptionSet(CreateStrictOptionSetArgumentParser())
            {
                Options.Help(help),
                Options.Verbose(verbose),
                Options.Debug,
                { "f|force"       , "force continue on errors", _ => force = true },
                { "x"             , "do not execute", _ => dontExecute = true },
                { "b|build"       , "build entirely to output directory; implies -f", _ => uncached = true },
                { "o|out|output=" , "output directory; implies -f", v => outDirPath = v },
                { "fx="           , $"target framework; default: {targetFramework.GetShortFolderName()}", v => targetFramework = NuGetFramework.Parse(v) },
            };

            var tail = options.Parse(args);

            if (verbose)
                Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));

            if (help || tail.Count == 0)
            {
                Help(options);
                return 0;
            }

            var subCommand = tail.First();
            var scriptArgs = tail.Skip(1);

            /*
            switch (subCommand)
            {
                case "foo":
                {
                    return FooCommand(scriptArgs);
                }
            }
            */

            var scriptPath = Path.GetFullPath(subCommand);

            var query = LinqPadQuery.Load(scriptPath);
            if (!query.IsLanguageSupported)
            {
                throw new NotSupportedException("Only LINQPad " +
                                                "C# Statements and Expression queries are fully supported " +
                                                "and C# Program queries partially in this version.");
            }

            var whackBang
                = query.Code.Lines().SkipWhile(string.IsNullOrWhiteSpace).FirstOrDefault() is string firstNonBlankLine
                ? Regex.Match(firstNonBlankLine, @"(?<=//#?![\x20\t]*).+").Value.Trim()
                : default;

            var template = whackBang.Split2(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Fold((t, _) => t ?? "template");

            var queryDir = new DirectoryInfo(Path.GetDirectoryName(query.FilePath));

            var searchPath =
                queryDir
                    .SelfAndParents()
                    .TakeUntil(d => File.Exists(Path.Combine(d.FullName, ".lplessroot")))
                    .Select(d => Path.Combine(d.FullName, ".lpless"))
                    .ToArray();

            IReadOnlyCollection<(string Name, IStreamable Content)> templateFiles
                = searchPath
                    .Select(d => Path.Combine(d, "templates", template))
                    .If(verbose, ss => ss.Do(() => Console.Error.WriteLine("Template searches:"))
                                         .WriteLine(Console.Error, s => "- " + s))
                    .FirstOrDefault(Directory.Exists) is string templateProjectPath
                ? Directory.GetFiles(templateProjectPath)
                           .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                    || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                           .Select(f => (Path.GetFileName(f), Streamable.Create(() => File.OpenRead(f))))
                           .ToArray()
                : default;

            if (templateFiles == null || templateFiles.Count == 0)
                throw new Exception("No template for running query.");

            var hashSource =
                MoreEnumerable
                    .From(() => new MemoryStream(Encoding.ASCII.GetBytes("1.0")))
                    .Concat(from rn in templateFiles.OrderBy(rn => rn.Name, StringComparer.OrdinalIgnoreCase)
                            select rn.Content.Open())
                    .Concat(MoreEnumerable.From(() => File.OpenRead(query.FilePath)))
                    .ToStreamable();

            string hash;
            using (var sha = SHA1.Create())
            using (var stream = hashSource.Open())
            {
                hash = BitConverter.ToString(sha.ComputeHash(stream))
                                   .Replace("-", string.Empty)
                                   .ToLowerInvariant();
            }

            string cacheId, cacheBaseDirPath;

            if (uncached)
            {
                cacheId = ".";
                cacheBaseDirPath = outDirPath ??
                                   Path.Combine(queryDir.FullName, Path.GetFileNameWithoutExtension(query.FilePath));
                force = true;
            }
            else
            {
                cacheId = hash;
                cacheBaseDirPath =
                    searchPath.Select(d => Path.Combine(d, "cache")).FirstOrDefault(Directory.Exists)
                    ?? Path.Combine(Path.GetTempPath(), "lpless", "cache");
            }

            var binDirPath = Path.Combine(cacheBaseDirPath, "bin", cacheId);
            var srcDirPath = Path.Combine(cacheBaseDirPath, "src", cacheId);
            var tmpDirPath = uncached ? binDirPath : Path.Combine(cacheBaseDirPath, "bin", "!" + cacheId);

            var exporting = outDirPath != null && !uncached;
            if (exporting)
            {
                if (Directory.Exists(outDirPath))
                    throw new Exception("The output directory already exists.");

                force = true;
            }

            {
                if (!force && Run() is int exitCode)
                    return exitCode;
            }

            try
            {
                Compile(query, targetFramework,
                        srcDirPath, tmpDirPath,
                        templateFiles,
                        verbose);

                if (tmpDirPath != binDirPath)
                {
                    if (!exporting && Directory.Exists(binDirPath))
                        Directory.Delete(binDirPath, true);

                    Directory.Move(tmpDirPath, binDirPath);
                }
            }
            catch
            {
                try
                {
                    if (tmpDirPath != binDirPath)
                        Directory.Delete(tmpDirPath);
                }
                catch { /* ignore */}
                throw;
            }

            {
                return Run() is int exitCode
                     ? exitCode
                     : throw new Exception("Internal error executing compilation.");
            }

            int? Run()
            {
                if (!Directory.Exists(binDirPath))
                    return null;

                const string depsJsonSuffix = ".deps.json";

                var binPath =
                    Directory.GetFiles(binDirPath, "*.json")
                             .Where(p => p.EndsWith(depsJsonSuffix, StringComparison.OrdinalIgnoreCase))
                             .Select(p => p.Substring(0, p.Length - depsJsonSuffix.Length))
                             .FirstOrDefault(p => p != null) is string s ? s + ".dll" : null;

                if (binPath == null)
                    return null;

                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    FileName        = "dotnet",
                    ArgumentList    = { binPath },
                };

                scriptArgs.ForEach(psi.ArgumentList.Add);

                string FormatCommandLine() =>
                    PasteArguments.Paste(psi.ArgumentList.Prepend(psi.FileName));

                if (verbose && !dontExecute)
                    Console.Error.WriteLine(FormatCommandLine());

                if (dontExecute)
                {
                    Console.WriteLine(FormatCommandLine());
                    return 0;
                }

                const string runLogFileName = "runs.log";
                var runLogPath = Path.Combine(binDirPath, runLogFileName);
                var runLogLockTimeout = TimeSpan.FromSeconds(5);
                var runLogLockName = string.Join("-", "lpless", hash, runLogFileName);

                void LogRun(FormattableString str) =>
                    File.AppendAllLines(runLogPath, Seq.Return(FormattableString.Invariant(str)));

                using (var runLogLock = ExternalLock.EnterLocal(runLogLockName, runLogLockTimeout))
                using (var process = Process.Start(psi))
                {
                    Debug.Assert(process != null);

                    var startTime = process.StartTime;
                    LogRun($"> {startTime:o} {process.Id}");
                    runLogLock.Dispose();

                    process.WaitForExit();
                    var endTime = DateTime.Now;

                    if (ExternalLock.TryEnterLocal(runLogLockName, runLogLockTimeout, out var mutex))
                    {
                        using (mutex)
                            LogRun($"< {endTime:o} {startTime:o}/{process.Id} {process.ExitCode}");
                    }

                    return process.ExitCode;
                }
            }
        }

        static class Options
        {
            public static Option Help(Ref<bool> value) =>
                new ActionOption("?|help|h", "prints out the options", _ => value.Value = true);

            public static Option Verbose(Ref<bool> value) =>
                new ActionOption("verbose|v", "enable additional output", _ => value.Value = true);

            public static readonly Option Debug =
                new ActionOption("d|debug", "debug break", vs => Debugger.Launch());
        }

        /*
        static int FooCommand(IEnumerable<string> args)
        {
            var help = Ref.Create(false);
            var verbose = Ref.Create(false);

            var options = new OptionSet(CreateStrictOptionSetArgumentParser())
            {
                Options.Help(help),
                Options.Verbose(verbose),
                Options.Debug,
            };

            var tail = options.Parse(args);

            if (verbose)
                Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));

            if (help)
            {
                Help(options);
                return 0;
            }

            Console.WriteLine("...");

            return 0;
        }
        */

        static void Compile(LinqPadQuery query,
            NuGetFramework targetFramework,
            string srcDirPath, string binDirPath,
            IEnumerable<(string Name, IStreamable Content)> templateFiles,
            bool verbose = false)
        {
            var writer = IndentingLineWriter.Create(Console.Error);

            if (verbose)
                writer.Write(query.MetaElement);

            var wc = new WebClient();

            NuGetVersion GetLatestPackageVersion(string id, bool isPrereleaseAllowed)
            {
                var latestVersion = Program.GetLatestPackageVersion(id, isPrereleaseAllowed, url =>
                {
                    if (verbose)
                        writer.WriteLine(url.OriginalString);
                    return wc.DownloadString(url);
                });
                if (verbose)
                    writer.WriteLine($"{id} -> {latestVersion}");
                return latestVersion;
            }

            var nrs =
                from nr in query.PackageReferences
                select new
                {
                    nr.Id,
                    nr.Version,
                    ActualVersion = nr.HasVersion
                                  ? Lazy.Value(nr.Version)
                                  : Lazy.Create(() => GetLatestPackageVersion(nr.Id, nr.IsPrereleaseAllowed)),
                    nr.IsPrereleaseAllowed,
                    Title = Seq.Return(nr.Id,
                                       nr.Version?.ToString(),
                                       nr.IsPrereleaseAllowed ? "(pre-release)" : null)
                               .Filter()
                               .ToDelimitedString(" "),
                };

            nrs = nrs.ToArray();

            var isNetCoreApp = ".NETCoreApp".Equals(targetFramework.Framework, StringComparison.OrdinalIgnoreCase);

            var defaultNamespaces
                = isNetCoreApp
                ? LinqPad.DefaultCoreNamespaces
                : LinqPad.DefaultNamespaces;

            var namespaces =
                from nss in new[]
                {
                    from ns in defaultNamespaces
                    select new
                    {
                        Name = ns,
                        IsDefaulted = true,
                    },
                    from ns in query.Namespaces
                    select new
                    {
                        Name = ns,
                        IsDefaulted = false,
                    },
                }
                from ns in nss
                select ns;

            namespaces = namespaces.ToArray();

            if (verbose)
            {
                if (nrs.Any())
                {
                    writer.WriteLine($"Packages ({nrs.Count():N0}):");
                    writer.WriteLines(from nr in nrs select "- " + nr.Title);
                }

                if (namespaces.Any())
                {
                    writer.WriteLine($"Imports ({query.Namespaces.Count:N0}):");
                    writer.WriteLines(from ns in namespaces select "- " + ns.Name + (ns.IsDefaulted ? "*" : null));
                }
            }

            if (verbose)
                writer.WriteLine($"Framework: {targetFramework}");

            var defaultReferences
                = isNetCoreApp
                ? Array.Empty<string>()
                : LinqPad.DefaultReferences;

            var references =
                defaultReferences
                    .Select(Choice.New.Choice1<string, PackageReference>)
                    .Concat(
                        from r in query.MetaElement.Elements("Reference")
                        select new
                        {
                            Relative = (string) r.Attribute("Relative"),
                            Path     = ((string) r).Trim(),
                        }
                        into r
                        where r.Path.Length > 0
                        select r.Relative?.Length > 0
                            ? r.Relative // prefer
                            : ResolveReferencePath(r.Path)
                        into r
                        select Choice.New.Choice1<string, PackageReference>(r))
                    .Concat(Enumerable.ToArray(
                        from r in nrs
                        select Choice.New.Choice2<string, PackageReference>(new PackageReference(r.Id, r.ActualVersion.Value, r.IsPrereleaseAllowed))));

            GenerateExecutable(srcDirPath, binDirPath, query,
                               from ns in namespaces select ns.Name,
                               references, templateFiles, verbose, writer);
        }

        static readonly char[] PathSeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        static string ResolveReferencePath(string path)
        {
            if (path.Length == 0 || path[0] != '<')
                return path;
            var endIndex = path.IndexOf('>');
            if (endIndex < 0)
                return path;
            var token = path.Substring(1, endIndex - 1);
            if (!DirPathByToken.TryGetValue(token, out var basePath))
                throw new Exception($"Unknown directory token \"{token}\" in reference \"{path}\".");
            return Path.Combine(basePath, path.Substring(endIndex + 1).TrimStart(PathSeparators));
        }

        static Dictionary<string, string> _dirPathByToken;

        public static Dictionary<string, string> DirPathByToken =>
            _dirPathByToken ?? (_dirPathByToken = ResolvedDirTokens().ToDictionary(StringComparer.OrdinalIgnoreCase));

        static IEnumerable<(string Token, string Path)> ResolvedDirTokens()
        {
            yield return ("RuntimeDirectory", RuntimeEnvironment.GetRuntimeDirectory());
            yield return ("ProgramFiles"    , Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            yield return ("ProgramFilesX86" , Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            yield return ("MyDocuments"     , Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        static readonly Encoding Utf8BomlessEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        [Flags]
        enum MainReturnTypeTraits
        {
            VoidTrait = 1,
            TaskTrait = 2,
            Int       = 0,
            Void      = VoidTrait,
            Task      = TaskTrait | VoidTrait,
            TaskOfInt = TaskTrait | Int,
        }

        static void GenerateExecutable(string srcDirPath, string binDirPath,
            LinqPadQuery query, IEnumerable<string> imports,
            IEnumerable<Choice<string, PackageReference>> references,
            IEnumerable<(string Name, IStreamable Content)> templateFiles,
            bool verbose, IndentingLineWriter writer)
        {
            // TODO error handling in generated code

            var workingDirPath = srcDirPath;
            if (!Directory.Exists(workingDirPath))
                Directory.CreateDirectory(workingDirPath);

            var rs = references.ToArray();

            var resourceNames =
                templateFiles
                    .ToDictionary(e => e.Name,
                                  e => e.Content,
                                  StringComparer.OrdinalIgnoreCase);

            var projectDocument =
                XDocument.Parse(resourceNames.Single(e => e.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Value.ReadText());

            var packageIdSet =
                rs.Select(e => e.Match(_ => null, r => r.Id))
                  .Where(e => e != null)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

            projectDocument
                .Descendants("PackageReference")
                .Where(e => packageIdSet.Contains((string) e.Attribute("Include")))
                .Remove();

            projectDocument.Element("Project").Add(
                new XElement("ItemGroup",
                    from r in rs
                    select r.Match(_ => null, p => p) into package
                    where package != null
                    select
                        new XElement("PackageReference",
                            new XAttribute("Include", package.Id),
                            new XAttribute("Version", package.Version))));

            var queryName = Path.GetFileNameWithoutExtension(query.FilePath);

            using (var xw = XmlWriter.Create(Path.Combine(workingDirPath, queryName + ".csproj"), new XmlWriterSettings
            {
                Encoding           = Utf8BomlessEncoding,
                Indent             = true,
                OmitXmlDeclaration = true,
            }))
            {
                projectDocument.WriteTo(xw);
            }

            const string mainFile = "Main.cs";
            var csFilePath = Path.Combine(workingDirPath, mainFile);
            File.Delete(csFilePath);

            var program = resourceNames[mainFile].ReadText();

            program =
                Detemplate(program, "imports",
                    imports.GroupBy(e => e, StringComparer.Ordinal)
                           .Select(ns => $"using {ns.First()};")
                           .ToDelimitedString(Environment.NewLine));

            program =
                Detemplate(program, "generator", () =>
                {
                    var versionInfo = CachedVersionInfo.Value;
                    return $"[assembly: System.CodeDom.Compiler.GeneratedCode({SyntaxFactory.Literal(versionInfo.ProductName)}, {SyntaxFactory.Literal(versionInfo.FileVersion)})]";
                });

            var source = query.Code;

            (string Source, IEnumerable<string> CompilationSymbols)
                GenerateProgram()
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(source);

                var main =
                    syntaxTree
                        .GetRoot()
                        .DescendantNodes().OfType<MethodDeclarationSyntax>()
                        .Single(md => "Main" == md.Identifier.Text);

                var updatedSource
                    = source.Substring(0, main.Identifier.Span.Start)
                    + "RunUserAuthoredQuery"
                    + source.Substring(main.Identifier.Span.End);

                var isAsync = main.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
                var isStatic = main.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

                MainReturnTypeTraits t;
                switch (main.ReturnType)
                {
                    case IdentifierNameSyntax ins when "Task".Equals(ins.Identifier.Value):
                        t = MainReturnTypeTraits.Task; break;
                    case GenericNameSyntax gns when "Task".Equals(gns.Identifier.Value):
                        t = MainReturnTypeTraits.TaskOfInt; break;
                    case PredefinedTypeSyntax pdts when pdts.Keyword.IsKind(SyntaxKind.VoidKeyword):
                        t = MainReturnTypeTraits.Void; break;
                    default:
                        t = MainReturnTypeTraits.Int; break;
                }

                var isVoid = t.HasFlag(MainReturnTypeTraits.VoidTrait);
                var isTask = t.HasFlag(MainReturnTypeTraits.TaskTrait);

                var hasArgs = main.ParameterList.Parameters.Any();

                /*

                [ static ] ( void | int | Task | Task<int> ) Main([ string[] args ]) {}

                static void Main()                     | STATIC, VOID
                static int Main()                      | STATIC,
                static void Main(string[] args)        | STATIC, VOID, ARGS
                static int Main(string[] args)         | STATIC, ARGS
                static Task Main()                     | STATIC, VOID, TASK
                static Task<int> Main()                | STATIC, TASK
                static Task Main(string[] args)        | STATIC, VOID, TASK, ARGS
                static Task<int> Main(string[] args)   | STATIC, TASK, ARGS
                void Main()                            | VOID
                int Main()                             |
                void Main(string[] args)               | VOID, ARGS
                int Main(string[] args)                | ARGS
                Task Main()                            | VOID, TASK
                Task<int> Main()                       | TASK
                Task Main(string[] args)               | VOID, TASK, ARGS
                Task<int> Main(string[] args)          | TASK, ARGS

                */

                return (
                    Detemplate(program, "program", updatedSource + Environment.NewLine),
                    Enumerable.Empty<string>()
                              .Concat(Ix.If(() => hasArgs , Seq.Return("ARGS")))
                              .Concat(Ix.If(() => isVoid  , Seq.Return("VOID")))
                              .Concat(Ix.If(() => isTask  , Seq.Return("TASK")))
                              .Concat(Ix.If(() => isAsync , Seq.Return("ASYNC")))
                              .Concat(Ix.If(() => isStatic, Seq.Return("STATIC"))));
            }

            program =
                Detemplate(program, "path-string",
                    SyntaxFactory.Literal(query.FilePath).ToString());

            program =
                Detemplate(program, "source-string",
                    () => SyntaxFactory.Literal(query.Code).ToString());

            var (body, symbols)
                = query.Language == LinqPadQueryLanguage.Expression
                ? (Detemplate(program, "expression", "#line 1" + Environment.NewLine + source),
                   Enumerable.Empty<string>())
                : query.Language == LinqPadQueryLanguage.Program
                ? GenerateProgram()
                : (Detemplate(program, "statements", "#line 1" + Environment.NewLine + source),
                   Enumerable.Empty<string>());

            var baseCompilationSymbol = "LINQPAD_" +
                ( query.Language == LinqPadQueryLanguage.Expression ? "EXPRESSION"
                : query.Language == LinqPadQueryLanguage.Program    ? "PROGRAM"
                : query.Language == LinqPadQueryLanguage.Statements ? "STATEMENTS"
                : throw new NotSupportedException()
                );

            if (body != null)
                File.WriteAllLines(csFilePath,
                    Seq.Return("#define LPLESS", "#define " + baseCompilationSymbol)
                       .Concat(from s in symbols select $"#define {baseCompilationSymbol}_{s}")
                       .Append(body)
                       .Append(string.Empty));

            foreach (var (name, content) in
                from f in resourceNames
                where !string.Equals(mainFile, f.Key, StringComparison.OrdinalIgnoreCase)
                   && !f.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                select f)
            {
                using (var s = content.Open())
                using (var w = File.Create(Path.Combine(srcDirPath, name)))
                    s.CopyTo(w);
            }

            // TODO User-supplied dotnet.cmd

            var publishArgs =
                Seq.Return("publish",
                           !verbose ? "-nologo" : null,
                           "-v", verbose ? "m" : "q",
                           "-c", "Release",
                           $"-p:{nameof(LinqPadless)}={CachedVersionInfo.Value.FileVersion}",
                           "-o", binDirPath)
                   .Filter()
                   .ToArray();

            if (verbose)
                writer.WriteLine(PasteArguments.Paste(publishArgs.Prepend("dotnet")));

            Spawn("dotnet",
                  publishArgs,
                  workingDirPath, writer.Indent(),
                  exitCode => new Exception($"dotnet publish ended with a non-zero exit code of {exitCode}."));
        }

        static string Detemplate(string template, string name, string replacement) =>
            Detemplate(template, name, Lazy.Value(replacement));

        static string Detemplate(string template, string name, Func<string> replacement) =>
            Detemplate(template, name, Lazy.Create(replacement));

        static string Detemplate(string template, string name, Lazy<string> replacement) =>
            Regex.Matches(template, @"
                     (?<= ^ | \r?\n )
                     [\x20\t]* // [\x20\t]* {% [\x20\t]*([a-z-]+)
                     (?: [\x20\t]* %}
                       | \s.*? // [\x20\t]* %}
                       )
                     [\x20\t]* (?=\r?\n)"
                     , RegexOptions.Singleline
                     | RegexOptions.IgnorePatternWhitespace)
                 .Aggregate((Index: 0, Text: string.Empty),
                            (s, m) =>
                                (m.Index + m.Length,
                                 s.Text + template.Substring(s.Index, m.Index - s.Index)
                                        + (string.Equals(name, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                                           ? replacement.Value
                                           : m.Value)),
                            s => s.Text + template.Substring(s.Index));

        static NuGetVersion GetLatestPackageVersion(string id, bool isPrereleaseAllowed, Func<Uri, string> downloader)
        {
            var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
            var d    = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices");
            var m    = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");

            var url = "https://www.nuget.org/api/v2/Search()"
                    + "?$orderby=Id"
                    + "&searchTerm='PackageId:" + Uri.EscapeDataString(id) + "'"
                    + "&targetFramework=''"
                    + "&includePrerelease=" + (isPrereleaseAllowed ? "true" : "false")
                    + "&$skip=0&$top=1&semVerLevel=2.0.0";

            var xml = downloader(new Uri(url));

            var versions =
                from e in XDocument.Parse(xml)
                                   .Element(atom + "feed")
                                   .Elements(atom + "entry")
                select NuGetVersion.Parse((string) e.Element(m + "properties")
                                                    .Element( d + "Version"));

            return versions.SingleOrDefault();
        }

        static void Spawn(string path, IEnumerable<string> args,
                          string workingDirPath, IndentingLineWriter writer,
                          Func<int, Exception> errorSelector)
        {
            var psi = new ProcessStartInfo
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                FileName               = path,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                WorkingDirectory       = workingDirPath,
            };

            args.ForEach(psi.ArgumentList.Add);

            using (var process = Process.Start(psi))
            {
                Debug.Assert(process != null);

                void OnStdDataReceived(object _, DataReceivedEventArgs e)
                {
                    if (e.Data == null)
                        return;
                    writer?.WriteLines(e.Data);
                }

                process.OutputDataReceived += OnStdDataReceived;
                process.ErrorDataReceived  += OnStdDataReceived;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                var exitCode = process.ExitCode;
                if (exitCode != 0)
                    throw errorSelector(exitCode);
            }
        }

        static readonly Lazy<FileVersionInfo> CachedVersionInfo = Lazy.Create(() => FileVersionInfo.GetVersionInfo(new Uri(typeof(Program).Assembly.CodeBase).LocalPath));
        static FileVersionInfo VersionInfo => CachedVersionInfo.Value;

        static void Help(Mono.Options.OptionSet options)
        {
            var name    = Lazy.Create(() => Path.GetFileNameWithoutExtension(VersionInfo.FileName));
            var opts    = Lazy.Create(() => options.WriteOptionDescriptionsReturningWriter(new StringWriter { NewLine = Environment.NewLine }).ToString());
            var logo    = Lazy.Create(() => new StringBuilder().AppendLine($"{VersionInfo.ProductName} (version {VersionInfo.FileVersion})")
                                                               .AppendLine(VersionInfo.LegalCopyright.Replace("\u00a9", "(C)"))
                                                               .ToString());

            using (var stream = GetManifestResourceStream("help.txt"))
            using (var reader = new StreamReader(stream))
            using (var e = reader.ReadLines())
            while (e.MoveNext())
            {
                var line = e.Current;
                line = Regex.Replace(line, @"\$([A-Z][A-Z_]*)\$", m =>
                {
                    switch (m.Groups[1].Value)
                    {
                        case "NAME": return name.Value;
                        case "LOGO": return logo.Value;
                        case "OPTIONS": return opts.Value;
                        default: return string.Empty;
                    }
                });

                if (line.Length > 0 && line[line.Length - 1] == '\n')
                    Console.Write(line);
                else
                    Console.WriteLine(line);
            }
        }

        static OptionSetArgumentParser CreateStrictOptionSetArgumentParser()
        {
            var hasTailStarted = false;
            return (impl, arg, context) =>
            {
                if (hasTailStarted) // once a tail, always a tail
                    return false;

                var isOption = impl(arg, context);
                if (!isOption)
                {
                    if (arg.Length > 0 && arg[0] == '-' && !hasTailStarted)
                        throw new Exception("Invalid argument: " + arg);
                    hasTailStarted = true;
                }

                return isOption;
            };
        }

        static string LoadTextResource(string name, Encoding encoding = null) =>
            LoadTextResource(typeof(Program), name, encoding);

        static string LoadTextResource(Type type, string name, Encoding encoding = null)
        {
            using (var stream = type != null
                              ? GetManifestResourceStream(type, name)
                              : GetManifestResourceStream(null, name))
            {
                Debug.Assert(stream != null);
                using (var reader = new StreamReader(stream, encoding ?? Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        static Stream GetManifestResourceStream(string name) =>
            GetManifestResourceStream(typeof(Program), name);

        static Stream GetManifestResourceStream(Type type, string name) =>
            type != null ? type.Assembly.GetManifestResourceStream(type, name)
                         : Assembly.GetCallingAssembly().GetManifestResourceStream(name);
    }
}
