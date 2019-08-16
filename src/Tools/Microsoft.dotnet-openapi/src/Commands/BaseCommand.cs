// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using Microsoft.Extensions.CommandLineUtils;

namespace Microsoft.DotNet.OpenApi.Commands
{
    internal abstract class BaseCommand : CommandLineApplication
    {
        protected string WorkingDirectory;

        private readonly HttpClient _httpClient;

        public const string OpenApiReference = "OpenApiReference";
        public const string OpenApiProjectReference = "OpenApiProjectReference";
        protected const string SourceUrlAttrName = "SourceUrl";

        internal const string PackageVersionUrl = "https://go.microsoft.com/fwlink/?linkid=2099561";

        public BaseCommand(CommandLineApplication parent, string name, HttpClient httpClient)
        {
            Parent = parent;
            Name = name;
            Out = parent.Out ?? Out;
            Error = parent.Error ?? Error;
            _httpClient = httpClient;

            ProjectFileOption = Option("-p|--updateProject", "The project file update.", CommandOptionType.SingleValue);

            Help = HelpOption("-?|-h|--help");
            if (Parent is Application)
            {
                WorkingDirectory = ((Application)Parent).WorkingDirectory;
            }
            else
            {
                WorkingDirectory = ((Application)Parent.Parent).WorkingDirectory;
            }

            OnExecute(ExecuteAsync);
        }

        public CommandOption ProjectFileOption { get; }

        internal CommandOption Help { get; }

        public TextWriter Warning
        {
            get { return Out; }
        }

        protected abstract Task<int> ExecuteCoreAsync();

        protected abstract bool ValidateArguments();

        private async Task<int> ExecuteAsync()
        {
            if (!ValidateArguments() || Help.HasValue())
            {
                ShowHelp();
                return 1;
            }

            return await ExecuteCoreAsync();
        }

        internal FileInfo ResolveProjectFile(CommandOption projectOption)
        {
            string project;
            if (projectOption.HasValue())
            {
                project = projectOption.Value();
                project = GetFullPath(project);
                if (!File.Exists(project))
                {
                    throw new ArgumentException($"The project '{project}' does not exist.");
                }
            }
            else
            {
                var projects = Directory.GetFiles(WorkingDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
                if (projects.Length == 0)
                {
                    throw new ArgumentException("No project files were found in the current directory. Either move to a new directory or provide the project explicitly");
                }
                if (projects.Length > 1)
                {
                    throw new ArgumentException("More than one project was found in this directory, either remove a duplicate or explicitly provide the project.");
                }

                project = projects[0];
            }

            return new FileInfo(project);
        }

        protected Project LoadProject(FileInfo projectFile)
        {
            var project = ProjectCollection.GlobalProjectCollection.LoadProject(
                projectFile.FullName,
                globalProperties: null,
                toolsVersion: null);
            project.ReevaluateIfNecessary();
            return project;
        }

        internal bool IsProjectFile(string file)
        {
            return File.Exists(Path.GetFullPath(file)) && file.EndsWith(".csproj");
        }

        internal bool IsUrl(string file)
        {
            return Uri.TryCreate(file, UriKind.Absolute, out var _) && file.StartsWith("http");
        }

        internal void AddServiceReference(
            string tagName,
            FileInfo projectFile,
            string sourceFile,
            string sourceUrl = null)
        {
            var project = LoadProject(projectFile);
            var items = project.GetItems(tagName);
            var fileItems = items.Where(i => string.Equals(GetFullPath(i.EvaluatedInclude), GetFullPath(sourceFile), StringComparison.Ordinal));

            if (fileItems.Count() >= 1)
            {
                Warning.Write($"One or more references to {sourceFile} already exist. Duplicate references could lead to unexpected behavior.");
                return;
            }

            if (sourceUrl != null)
            {
                if (items.Any(i => string.Equals(i.GetMetadataValue(SourceUrlAttrName), sourceUrl)))
                {
                    Warning.Write($"A reference to '{sourceUrl}' already exists in '{project.FullPath}'.");
                    return;
                }
            }

            if (fileItems.Count() == 0)
            {
                var metadata = new Dictionary<string, string>();

                if (!string.IsNullOrEmpty(sourceUrl))
                {
                    metadata[SourceUrlAttrName] = sourceUrl;
                }

                project.AddElementWithAttributes(tagName, sourceFile, metadata);
            }
            else
            {
                Warning.Write($"A reference to '{sourceFile}' already exists in '{project.FullPath}'.");
                return;
            }
        }

        internal async Task DownloadToFileAsync(string url, string destinationPath, bool overwrite)
        {
            Application application;
            if (Parent is Application)
            {
                application = (Application)Parent;
            }
            else
            {
                application = (Application)Parent.Parent;
            }

            using var content = await application.DownloadProvider(url);
            await WriteToFileAsync(content, destinationPath, overwrite);
        }

        internal CodeGenerator GetCodeGenerator(CommandOption codeGeneratorOption)
        {
            CodeGenerator codeGenerator;
            if (codeGeneratorOption.HasValue())
            {
                codeGenerator = Enum.Parse<CodeGenerator>(codeGeneratorOption.Value());
            }
            else
            {
                codeGenerator = CodeGenerator.NSwagCSharp;
            }

            return codeGenerator;
        }

        internal void ValidateCodeGenerator(CommandOption codeGeneratorOption)
        {
            if (codeGeneratorOption.HasValue())
            {
                var value = codeGeneratorOption.Value();
                if (!Enum.TryParse<CodeGenerator>(value, out CodeGenerator _))
                {
                    throw new ArgumentException($"Invalid value '{value}' given as code generator.");
                }
            }
        }

        internal async Task EnsurePackagesInProjectAsync(FileInfo projectFile, CodeGenerator codeGenerator)
        {
            var packages = await ResolvePackageVersionsAsync() ?? GetServicePackages(codeGenerator);
            foreach (var (packageId, version) in packages)
            {
                var args = new[] {
                    "add",
                    "package",
                    packageId,
                    "--version",
                    version,
                    "--no-restore"
                };

                var muxer = DotNetMuxer.MuxerPathOrDefault();
                if (string.IsNullOrEmpty(muxer))
                {
                    throw new ArgumentException($"dotnet was not found on the path.");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = muxer,
                    Arguments = string.Join(" ", args),
                    WorkingDirectory = projectFile.Directory.FullName,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };

                var process = Process.Start(startInfo);

                var timeout = 20;
                if (!process.WaitForExit(timeout * 1000))
                {
                    throw new ArgumentException($"Adding package `{packageId}` to `{projectFile.Directory}` took longer than {timeout} seconds.");
                }

                if (process.ExitCode != 0)
                {
                    Out.Write(process.StandardOutput.ReadToEnd());
                    Error.Write(process.StandardError.ReadToEnd());
                    throw new ArgumentException($"Could not add package `{packageId}` to `{projectFile.Directory}`");
                }
            }
        }

        internal string GetFullPath(string path)
        {
            return Path.IsPathFullyQualified(path)
                ? path
                : Path.GetFullPath(path, WorkingDirectory);
        }

        private async Task<IDictionary<string, string>> ResolvePackageVersionsAsync()
        {
            /* Example Json content
             {
              "Version" : "1.0",
              "Packages"  :  {
                "Microsoft.Azure.SignalR": "1.1.0-preview1-10442",
                "Grpc.AspNetCore.Server": "0.1.22-pre2",
                "Grpc.Net.ClientFactory": "0.1.22-pre2",
                "Google.Protobuf": "3.8.0",
                "Grpc.Tools": "1.22.0",
                "NSwag.ApiDescription.Client": "13.0.3",
                "Microsoft.Extensions.ApiDescription.Client": "0.3.0-preview7.19365.7",
                "Newtonsoft.Json": "12.0.2"
              }
            }*/
            try
            {
                using var packageVersionStream = await _httpClient.GetStreamAsync(PackageVersionUrl);
                using var packageVersionDocument = await JsonDocument.ParseAsync(packageVersionStream);
                var packageVersionsElement = packageVersionDocument.RootElement.GetProperty("Packages");
                var packageVersionsDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var packageVersion in packageVersionsElement.EnumerateObject())
                {
                    packageVersionsDictionary[packageVersion.Name] = packageVersion.Value.GetString();
                }

                return packageVersionsDictionary;
            }
            catch
            {
                // TODO (johluo): Consider logging a message indicating what went wrong and actions, if any, to be taken to resolve possible issues.
                // Currently not logging anything since the fwlink is not published yet.
                return null;
            }
        }

        private static IDictionary<string, string> GetServicePackages(CodeGenerator type)
        {
            var name = Enum.GetName(typeof(CodeGenerator), type);
            var attributes = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
            var attribute = attributes.Single(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase));

            var packages = attribute?.Value?.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var result = new Dictionary<string, string>();
            if (packages != null)
            {
                foreach (var package in packages)
                {
                    var tmp = package.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    Debug.Assert(tmp.Length == 2);
                    result[tmp[0]] = tmp[1];
                }
            }

            return result;
        }

        private static byte[] GetHash(Stream stream)
        {
            SHA256 algorithm;
            try
            {
                algorithm = SHA256.Create();
            }
            catch (TargetInvocationException)
            {
                // SHA256.Create is documented to throw this exception on FIPS-compliant machines. See
                // https://msdn.microsoft.com/en-us/library/z08hz7ad Fall back to a FIPS-compliant SHA256 algorithm.
                algorithm = new SHA256CryptoServiceProvider();
            }

            using (algorithm)
            {
                return algorithm.ComputeHash(stream);
            }
        }

        private async Task WriteToFileAsync(Stream content, string destinationPath, bool overwrite)
        {
            if (content.CanSeek)
            {
                content.Seek(0, SeekOrigin.Begin);
            }

            destinationPath = GetFullPath(destinationPath);
            var destinationExists = File.Exists(destinationPath);
            if (destinationExists && !overwrite)
            {
                throw new ArgumentException($"File '{destinationPath}' already exists. Aborting to avoid conflicts.");
            }

            await Out.WriteLineAsync($"Downloading to '{destinationPath}'.");
            var reachedCopy = false;
            try
            {
                if (destinationExists)
                {
                    // Check hashes before using the downloaded information.
                    var downloadHash = GetHash(content);

                    byte[] destinationHash;
                    using (var destinationStream = File.OpenRead(destinationPath))
                    {
                        destinationHash = GetHash(destinationStream);
                    }

                    var sameHashes = downloadHash.Length == destinationHash.Length;
                    for (var i = 0; sameHashes && i < downloadHash.Length; i++)
                    {
                        sameHashes = downloadHash[i] == destinationHash[i];
                    }

                    if (sameHashes)
                    {
                        await Out.WriteLineAsync($"Not overwriting existing and matching file '{destinationPath}'.");
                        return;
                    }
                }
                else
                {
                    // May need to create directory to hold the file.
                    var destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }
                }

                // Create or overwrite the destination file.
                reachedCopy = true;
                using var fileStream = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write);
                fileStream.Seek(0, SeekOrigin.Begin);
                if (content.CanSeek)
                {
                    content.Seek(0, SeekOrigin.Begin);
                }
                await content.CopyToAsync(fileStream);
            }
            catch (Exception ex)
            {
                await Error.WriteLineAsync($"Downloading failed.");
                await Error.WriteLineAsync(ex.ToString());
                if (reachedCopy)
                {
                    File.Delete(destinationPath);
                }
            }
        }
    }
}
