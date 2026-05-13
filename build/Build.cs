using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Git;
using Octokit;
using Serilog;
using ICSharpCode.SharpZipLib.Zip;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuke.Common.CI.GitHubActions;
using System.IO;
using System.Collections.Generic;
using System.Linq;

partial class Build : NukeBuild
{
    readonly ProductHeaderValue ProductHeader = new("BepInExPack-Silksong-Build");

    readonly AbsolutePath ObjDir = RootDirectory / "obj";
    readonly AbsolutePath BuildStaging = RootDirectory / "obj" / "staging";
    readonly AbsolutePath BuildAssets = RootDirectory / "obj" / "assets";

    readonly AbsolutePath BinDir = RootDirectory / "bin";

    readonly string BepInExVersion = "5.4.23.4";

    [NuGetPackage(
        packageId: "minver-cli",
        packageExecutable: "minver.dll",
        Framework = "net10.0"
    )]
    readonly Tool MinVer;

    [NuGetPackage(
        packageId: "tcli",
        packageExecutable: "tcli.dll",
        Framework = "net7.0"
    )]
    readonly Tool Tcli;

    [Parameter, Secret]
    readonly string GitHubToken;

    [Parameter, Secret]
    readonly string ThunderstoreToken;

    [Parameter]
    readonly AbsolutePath ArtifactDir;

    public static int Main() => Execute<Build>(x => x.Package);

    Target Clean => _ => _
        .Executes(() =>
        {
            ObjDir.CreateOrCleanDirectory();
            BinDir.CreateOrCleanDirectory();
        });

    Target Package => _ => _
        .After(Clean)
        .Executes(async () =>
        {
            ObjDir.CreateOrCleanDirectory();
            BinDir.CreateDirectory();

            HttpClient httpClient = new();
            GitHubClient client = new(ProductHeader);
            Release release = await client.Repository.Release.Get("BepInEx", "BepInEx", "v" + BepInExVersion);
            await Parallel.ForEachAsync(release.Assets, async (asset, ct) =>
            {
                ct.ThrowIfCancellationRequested();

                Match match = AssetMatcher().Match(asset.Name);
                if (match.Success && match.Groups["architecture"].Value == "x64")
                {
                    string targetName = match.Groups["platform"].Value;
                    string targetFileName = $"{targetName}.zip";
                    AbsolutePath unzippedTargetPath = BuildStaging / targetName;
                    AbsolutePath assetTargetPath = BuildStaging / targetFileName;
                    Log.Information("Downloading asset {Url} to {TargetPath} from {Url}", asset.BrowserDownloadUrl, unzippedTargetPath);
                    byte[] resp = await httpClient.GetByteArrayAsync(asset.BrowserDownloadUrl);
                    assetTargetPath.WriteAllBytes(resp);
                    assetTargetPath.UncompressTo(unzippedTargetPath);
                }
            });

            // assemble our package
            // 1. Main content from win
            (BuildStaging / "win").Copy(BuildAssets);
            // 2. platform specific libraries and *nix launcher from linux
            (BuildStaging / "linux" / "libdoorstop.so").CopyToDirectory(BuildAssets);
            (BuildStaging / "linux" / "run_bepinex.sh").CopyToDirectory(BuildAssets);
            // 3. platform specific libraries from mac
            (BuildStaging / "macos" / "libdoorstop.dylib").CopyToDirectory(BuildAssets);

            // 4. apply patches
            GitTasks.Git("apply patches/run_bepinex.patch");

            string version = MinVer("--tag-prefix v").StdToText().Split('-')[0];
            Log.Information("Packaging version {version}", version);

            Tcli($"build --package-version {version}");

            Log.Information("Finalizing permissions...");
            string artifactName = $"silksong_modding-BepInExPack_Silksong-{version}.zip";
            AbsolutePath sourcePath = ObjDir / artifactName;
            AbsolutePath artifactPath = BinDir / artifactName;
            using ZipFile source = new(sourcePath);
            using ZipOutputStream dest = new(artifactPath.ToFileInfo().Create());
            // copy and update unix permissions
            foreach (ZipEntry entry in source)
            {
                bool isDir = entry.IsDirectory;
                if (isDir)
                {
                    continue;
                }

                ZipEntry newEntry = new(entry.Name)
                {
                    // 755, normal file
                    ExternalFileAttributes = 0x81ED << 16,
                    HostSystem = (int)HostSystemID.Unix,
                    DateTime = entry.DateTime,
                    Comment = entry.Comment,
                    CompressionMethod = entry.Size < 100 ? CompressionMethod.Stored : entry.CompressionMethod,
                };

                dest.PutNextEntry(newEntry);
                using System.IO.Stream entryStream = source.GetInputStream(entry);
                entryStream.CopyTo(dest);
                dest.CloseEntry();
            }
            // add directory permissions
            foreach (string path in DirectoryEntries)
            {
                ZipEntry newEntry = new(path)
                {
                    // 755, directory
                    ExternalFileAttributes = 0x41ED << 16,
                    HostSystem = (int)HostSystemID.Unix,
                    CompressionMethod = CompressionMethod.Stored,
                };
                dest.PutNextEntry(newEntry);
                dest.CloseEntry();
            }
        });

    Target Publish => _ => _
        .Requires(() => GitHubToken)
        .Requires(() => ThunderstoreToken)
        .Requires(() => ArtifactDir)
        .Executes(async () =>
        {
            Assert.DirectoryExists(ArtifactDir);
            IReadOnlyCollection<AbsolutePath> possibleArtifacts = ArtifactDir.GlobFiles("*.zip");
            Assert.Count(possibleArtifacts, 1);
            AbsolutePath artifactPath = possibleArtifacts.First();

            bool isDryrun = GitHubActions.Instance == null;
            if (isDryrun)
            {
                Log.Information("Running outside GitHub Actions, running in dryrun mode");
            }

            string tag;
            if (isDryrun)
            {
                tag = "refs/tags/v0.0.0"[10..];
            }
            else
            {
                Assert.True(GitHubActions.Instance.Ref.StartsWith("refs/tags/"), "Publish task may only be run on tag pushes");
                tag = GitHubActions.Instance.Ref[10..];
            }

            GitHubClient client = new(ProductHeader)
            {
                Credentials = new Credentials(GitHubToken)
            };

            Log.Information("Publishing {path} to Thunderstore", artifactPath);
            if (!isDryrun)
            {
                Tcli($"publish --file {artifactPath} --token {ThunderstoreToken}");
            }
            Log.Information("Publishing {path} to GitHub at {tag}", artifactPath, tag);
            if (!isDryrun)
            {
                if (GitHubActions.Instance.Repository.Split('/') is not [string owner, string repo])
                {
                    Assert.Fail("Could not extract owner and repo");
                    return;
                }
                Release release = await client.Repository.Release.Create(owner, repo, new NewRelease(tag)
                {
                    GenerateReleaseNotes = true,
                    Draft = true
                });
                using FileStream fs = artifactPath.ToFileInfo().OpenRead();
                await client.Repository.Release.UploadAsset(release, new ReleaseAssetUpload()
                {
                    FileName = artifactPath.Name,
                    ContentType = "application/zip",
                    RawData = fs,
                });
                await client.Repository.Release.Edit(owner, repo, release.Id, new ReleaseUpdate()
                {
                    Draft = false,
                    MakeLatest = MakeLatestQualifier.True,
                });
            }
        });

    private static readonly string[] DirectoryEntries = ["BepInExPack/", "BepInExPack/BepInEx/", "BepInExPack/BepInEx/config/", "BepInExPack/BepInEx/core/", "BepInExPack/BepInEx/plugins/"];

    [GeneratedRegex(@"^BepInEx_(?<platform>\w+)_(?<architecture>\w+)_(?<version>.+)\.zip$")]
    private static partial Regex AssetMatcher();
}
