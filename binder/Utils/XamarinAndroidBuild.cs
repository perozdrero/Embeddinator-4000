using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Mono.Cecil;
using Xamarin.Android.Tools;

namespace Embeddinator
{
    /// <summary>
    /// Contains everything MSBuild-related for Xamarin.Android
    /// </summary>
    static class XamarinAndroidBuild
    {
        public const string IntermediateDir = "obj";
        public const string ResourcePaths = "resourcepaths.cache";

        const string LibraryProjectDir = "lp";
        const string ImportsDirectory = "jl";
        const string LinkMode = "SdkOnly";

        static ProjectRootElement CreateProject()
        {
            var monoDroidPath = XamarinAndroid.Path;
            var msBuildPath = Path.Combine(monoDroidPath, "lib", "xbuild", "Xamarin", "Android");
            if (!msBuildPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.OrdinalIgnoreCase))
                msBuildPath = msBuildPath + Path.DirectorySeparatorChar;

            var project = ProjectRootElement.Create();
            project.AddProperty("Configuration", "Release");
            project.AddProperty("Platform", "AnyCPU");
            project.AddProperty("PlatformTarget", "AnyCPU");
            project.AddProperty("OutputPath", "bin\\Release");
            project.AddProperty("TargetFrameworkDirectory", string.Join(";", XamarinAndroid.TargetFrameworkDirectories));
            project.AddImport(ProjectCollection.Escape(Path.Combine(msBuildPath, "Xamarin.Android.CSharp.targets")));

            return project;
        }

        static void ResolveAssemblies(ProjectTargetElement target, List<IKVM.Reflection.Assembly> assemblies)
        {
            var resolveAssemblies = target.AddTask("ResolveAssemblies");
            var assemblyPaths = assemblies.Select(a => a.Location).ToList();
            //NOTE: [Export] requires Mono.Android.Export.dll
            assemblyPaths.Add(XamarinAndroid.FindAssembly("Mono.Android.Export.dll"));

            resolveAssemblies.SetParameter("Assemblies", string.Join(";", assemblyPaths));
            resolveAssemblies.SetParameter("LinkMode", LinkMode);
            resolveAssemblies.SetParameter("ReferenceAssembliesDirectory", "$(TargetFrameworkDirectory)");
            resolveAssemblies.AddOutputItem("ResolvedAssemblies", "ResolvedAssemblies");
            resolveAssemblies.AddOutputItem("ResolvedUserAssemblies", "ResolvedUserAssemblies");
            resolveAssemblies.AddOutputItem("ResolvedFrameworkAssemblies", "ResolvedFrameworkAssemblies");
        }

        /// <summary>
        /// Generates a Package.proj file for MSBuild to invoke
        /// - Generates Resource.designer.dll for rewiring resource values from the final Java project
        /// - Links .NET assemblies and places output into /android/assets/assemblies
        /// - Extracts assets and resources from Android library projects into /obj/
        /// - Copies assets and resources into AAR
        /// - Invokes aapt to generate R.txt
        /// - One day I would like to get rid of the temp files, but I could not get the MSBuild APIs to work in-process
        /// </summary>
        public static string GeneratePackageProject(List<IKVM.Reflection.Assembly> assemblies, Options options)
        {
            var mainAssembly = assemblies[0].Location;
            var outputDirectory = Path.GetFullPath(options.OutputDir);
            var assembliesDirectory = Path.Combine(outputDirectory, "android", "assets", "assemblies");

            var androidDir = Path.Combine(outputDirectory, "android");
            var assetsDir = Path.Combine(androidDir, "assets");
            var resourceDir = Path.Combine(androidDir, "res");
            var manifestPath = Path.Combine(androidDir, "AndroidManifest.xml");
            var packageName = Generators.JavaGenerator.GetNativeLibPackageName(mainAssembly);
            var project = CreateProject();
            var target = project.AddTarget("Build");
            var group = project.AddItemGroup();
            group.AddItem("LinkDescription", "LinkDescription.xml");

            //ResolveAssemblies Task
            ResolveAssemblies(target, assemblies);

            //LinkAssemblies Task
            var linkAssemblies = target.AddTask("LinkAssemblies");
            linkAssemblies.SetParameter("UseSharedRuntime", "False");
            linkAssemblies.SetParameter("LinkMode", LinkMode);
            linkAssemblies.SetParameter("LinkDescriptions", "@(LinkDescription)");
            linkAssemblies.SetParameter("DumpDependencies", "True");
            linkAssemblies.SetParameter("ResolvedAssemblies", "@(ResolvedAssemblies);" + Path.Combine(outputDirectory, "Resource.designer.dll"));
            linkAssemblies.SetParameter("MainAssembly", mainAssembly);
            linkAssemblies.SetParameter("OutputDirectory", assembliesDirectory);

            //If not Debug, delete our PDB files
            if (!options.Compilation.DebugMode)
            {
                var itemGroup = target.AddItemGroup();
                itemGroup.AddItem("PdbFilesToDelete", Path.Combine(assembliesDirectory, "*.pdb"));

                var delete = target.AddTask("Delete");
                delete.SetParameter("Files", "@(PdbFilesToDelete)");
            }

            //Aapt Task to generate R.txt
            var aapt = target.AddTask("Aapt");
            aapt.Condition = $"Exists('{resourceDir}')";
            aapt.SetParameter("ImportsDirectory", outputDirectory);
            aapt.SetParameter("OutputImportDirectory", outputDirectory);
            aapt.SetParameter("ManifestFiles", manifestPath);
            aapt.SetParameter("ApplicationName", packageName);
            aapt.SetParameter("JavaPlatformJarPath", Path.Combine(XamarinAndroid.PlatformDirectory, "android.jar"));
            aapt.SetParameter("JavaDesignerOutputDirectory", outputDirectory);
            aapt.SetParameter("AssetDirectory", assetsDir);
            aapt.SetParameter("ResourceDirectory", resourceDir);
            aapt.SetParameter("ToolPath", XamarinAndroid.AndroidSdk.GetBuildToolsPaths().First());
            aapt.SetParameter("ToolExe", "aapt");
            aapt.SetParameter("ApiLevel", XamarinAndroid.TargetSdkVersion.ToString());
            aapt.SetParameter("ExtraArgs", "--output-text-symbols " + androidDir);

            //There is an extra /manifest/AndroidManifest.xml file created
            var removeDir = target.AddTask("RemoveDir");
            removeDir.SetParameter("Directories", Path.Combine(androidDir, "manifest"));

            //NOTE: might avoid the temp file later
            var projectFile = Path.Combine(outputDirectory, "Package.proj");
            project.Save(projectFile);
            var xmlFile = Path.Combine(outputDirectory, "LinkDescription.xml");
            var xml = String.Join(
    Environment.NewLine,
    "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>",
    "<linker>",
    "    <assembly fullname=\"mscorlib\">",
    "        <type fullname=\"System.String\">",
    "            <method name=\"Compare\"></method>",
    "            <method name=\"CompareTo\"></method>",
    "            <method name=\"ToUpper\"></method>",
    "            <method name=\"ToLower\"></method>",
    "        </type>",
    "        <type fullname=\"System.DateTime\" preserve=\"methods\" />",
    "        <type fullname=\"System.Math\">",
    "            <method name=\"Abs\"></method>",
    "            <method name=\"Max\"></method>",
    "            <method name=\"Min\"></method>",
    "            <method name=\"Round\"></method>",
    "        </type>",
    "    </assembly>",
    "    <assembly fullname=\"System.Core\">",
    "        <type fullname=\"System.Linq.Expressions.Expression`1\"></type>",
    "        <type fullname=\"System.Linq.Queryable\"></type>",
    "    </assembly>",
    "</linker>",
    "");
            File.WriteAllText(xmlFile, xml, Encoding.UTF8);
            return projectFile;
        }

        /// <summary>
        /// Generates AndroidManifest.xml
        /// </summary>
        public static void GenerateAndroidManifest(IList<IKVM.Reflection.Assembly> assemblies, string path, bool includeProvider = true)
        {
            var mainAssembly = assemblies[0].Location;
            var packageName = Generators.JavaGenerator.GetNativeLibPackageName(mainAssembly);
            var className = Generators.JavaNative.GetNativeLibClassName(mainAssembly);
            string provider = string.Empty;
            if (includeProvider)
            {
                provider = $"<provider android:name=\"mono.embeddinator.AndroidRuntimeProvider\" android:exported=\"false\" android:initOrder=\"{int.MaxValue}\" android:authorities=\"${{applicationId}}.mono.embeddinator.AndroidRuntimeProvider.__mono_init__\" />";
            }

            File.WriteAllText(path,
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<manifest xmlns:android=""http://schemas.android.com/apk/res/android"" package=""{packageName}"">
    <uses-sdk android:minSdkVersion=""{XamarinAndroid.MinSdkVersion}"" android:targetSdkVersion=""{XamarinAndroid.TargetSdkVersion}"" />
    <application>
        <meta-data android:name=""mono.embeddinator.classname"" android:value=""{packageName}.{className}"" />
        {provider}
    </application>
</manifest>");
        }

        /// <summary>
        /// Generates a GenerateJavaStubs.proj file for MSBuild to invoke
        /// - Generates Java source code for each C# class that subclasses Java.Lang.Object
        /// - Generates AndroidManifest.xml
        /// - One day I would like to get rid of the temp files, but I could not get the MSBuild APIs to work in-process
        /// </summary>
        public static string GenerateJavaStubsProject(List<IKVM.Reflection.Assembly> assemblies, string outputDirectory)
        {
            var mainAssembly = assemblies[0].Location;
            outputDirectory = Path.GetFullPath(outputDirectory);

            var intermediateDir = Path.Combine(outputDirectory, IntermediateDir);
            var androidDir = Path.Combine(outputDirectory, "android");
            var javaSourceDir = Path.Combine(outputDirectory, "src");
            var assetsDir = Path.Combine(androidDir, "assets");
            var resourceDir = Path.Combine(androidDir, "res");
            var manifestPath = Path.Combine(androidDir, "AndroidManifest.xml");
            var packageName = Generators.JavaGenerator.GetNativeLibPackageName(mainAssembly);

            if (!Directory.Exists(androidDir))
                Directory.CreateDirectory(androidDir);

            //AndroidManifest.xml template
            GenerateAndroidManifest(assemblies, manifestPath, false);

            var project = CreateProject();
            var target = project.AddTarget("Build");

            //ResolveAssemblies Task
            ResolveAssemblies(target, assemblies);

            //GenerateJavaStubs Task
            var generateJavaStubs = target.AddTask("GenerateJavaStubs");
            generateJavaStubs.SetParameter("ResolvedAssemblies", "@(ResolvedAssemblies)");
            generateJavaStubs.SetParameter("ResolvedUserAssemblies", "@(ResolvedUserAssemblies)");
            generateJavaStubs.SetParameter("ManifestTemplate", manifestPath);
            generateJavaStubs.SetParameter("MergedAndroidManifestOutput", manifestPath);
            generateJavaStubs.SetParameter("AndroidSdkPlatform", XamarinAndroid.TargetSdkVersion.ToString()); //TODO: should be an option
            generateJavaStubs.SetParameter("AndroidSdkDir", XamarinAndroid.AndroidSdk.AndroidSdkPath);
            generateJavaStubs.SetParameter("OutputDirectory", outputDirectory);
            generateJavaStubs.SetParameter("ResourceDirectory", "$(MonoAndroidResDirIntermediate)");
            generateJavaStubs.SetParameter("AcwMapFile", "$(MonoAndroidIntermediate)acw-map.txt");

            //ResolveLibraryProjectImports Task, extracts Android resources
            var resolveLibraryProject = target.AddTask("ResolveLibraryProjectImports");
            resolveLibraryProject.SetParameter("Assemblies", "@(ResolvedUserAssemblies)");
            resolveLibraryProject.SetParameter("AssemblyIdentityMapFile", Path.Combine(intermediateDir, LibraryProjectDir, "map.cache"));
            resolveLibraryProject.SetParameter("CacheFile", Path.Combine(intermediateDir, "libraryprojectimports.cache"));
            resolveLibraryProject.SetParameter("UseShortFileNames", "False");
            resolveLibraryProject.SetParameter("ImportsDirectory", ImportsDirectory);
            resolveLibraryProject.SetParameter("OutputDirectory", intermediateDir);
            resolveLibraryProject.SetParameter("OutputImportDirectory", Path.Combine(intermediateDir, LibraryProjectDir));
            resolveLibraryProject.AddOutputItem("ResolvedAssetDirectories", "ResolvedAssetDirectories");
            resolveLibraryProject.AddOutputItem("ResolvedResourceDirectories", "ResolvedResourceDirectories");

            //GetAdditionalResourcesFromAssemblies Task, for JavaLibraryReferenceAttribute, etc.
            var getAdditionalResources = target.AddTask("GetAdditionalResourcesFromAssemblies");
            getAdditionalResources.SetParameter("AndroidSdkDirectory", XamarinAndroid.AndroidSdk.AndroidSdkPath);
            getAdditionalResources.SetParameter("AndroidNdkDirectory", XamarinAndroid.AndroidSdk.AndroidNdkPath);
            getAdditionalResources.SetParameter("Assemblies", "@(ResolvedUserAssemblies)");
            getAdditionalResources.SetParameter("CacheFile", Path.Combine(intermediateDir, ResourcePaths));
            getAdditionalResources.SetParameter("DesignTimeBuild", "False");

            //Create ItemGroup of Android files
            var androidResources = target.AddItemGroup();
            foreach (var assembly in assemblies)
            {
                var assemblyName = assembly.GetName().Name;
                androidResources.AddItem("AndroidAsset", Path.Combine(intermediateDir, LibraryProjectDir, assemblyName, ImportsDirectory, "assets", "**", "*"));
                androidResources.AddItem("AndroidJavaSource", Path.Combine(intermediateDir, LibraryProjectDir, assemblyName, ImportsDirectory, "java", "**", "*.java"));
                androidResources.AddItem("AndroidResource", Path.Combine(intermediateDir, LibraryProjectDir, assemblyName, ImportsDirectory, "res", "**", "*"));
            }

            //Copy Task, to copy AndroidAsset files
            var copy = target.AddTask("Copy");
            copy.SetParameter("SourceFiles", "@(AndroidAsset)");
            copy.SetParameter("DestinationFiles", $"@(AndroidAsset->'{assetsDir + Path.DirectorySeparatorChar}%(RecursiveDir)%(Filename)%(Extension)')");

            //Copy Task, to copy AndroidResource files
            copy = target.AddTask("Copy");
            copy.SetParameter("SourceFiles", "@(AndroidResource)");
            copy.SetParameter("DestinationFiles", $"@(AndroidResource->'{resourceDir + Path.DirectorySeparatorChar}%(RecursiveDir)%(Filename)%(Extension)')");

            //Copy Task, to copy AndroidJavaSource files
            copy = target.AddTask("Copy");
            copy.SetParameter("SourceFiles", "@(AndroidJavaSource)");
            copy.SetParameter("DestinationFiles", $"@(AndroidJavaSource->'{javaSourceDir + Path.DirectorySeparatorChar}%(RecursiveDir)%(Filename)%(Extension)')");

            //XmlPoke to fix up AndroidManifest
            var xmlPoke = target.AddTask("XmlPoke");
            xmlPoke.SetParameter("XmlInputPath", manifestPath);
            xmlPoke.SetParameter("Query", "/manifest/@package");
            xmlPoke.SetParameter("Value", packageName);

            //android:name
            xmlPoke = target.AddTask("XmlPoke");
            xmlPoke.SetParameter("XmlInputPath", manifestPath);
            xmlPoke.SetParameter("Namespaces", "<Namespace Prefix='android' Uri='http://schemas.android.com/apk/res/android' />");
            xmlPoke.SetParameter("Query", "/manifest/application/provider/@android:name");
            xmlPoke.SetParameter("Value", "mono.embeddinator.AndroidRuntimeProvider");

            //android:authorities
            xmlPoke = target.AddTask("XmlPoke");
            xmlPoke.SetParameter("XmlInputPath", manifestPath);
            xmlPoke.SetParameter("Namespaces", "<Namespace Prefix='android' Uri='http://schemas.android.com/apk/res/android' />");
            xmlPoke.SetParameter("Query", "/manifest/application/provider/@android:authorities");
            xmlPoke.SetParameter("Value", "${applicationId}.mono.embeddinator.AndroidRuntimeProvider.__mono_init__");

            //NOTE: might avoid the temp file later
            var projectFile = Path.Combine(outputDirectory, "GenerateJavaStubs.proj");
            project.Save(projectFile);
            return projectFile;
        }

        /// <summary>
        /// For each linked assembly:
        /// - We need to extract __AndroidNativeLibraries__.zip into the AAR directory
        /// - We need to strip __AndroidLibraryProjects__.zip and __AndroidNativeLibraries__.zip
        /// </summary>
        public static void ProcessAssemblies(string outputDirectory)
        {
            var assembliesDir = Path.Combine(outputDirectory, "android", "assets", "assemblies");
            var jniDir = Path.Combine(outputDirectory, "android", "jni");
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(assembliesDir);

            foreach (var assemblyFile in Directory.GetFiles(assembliesDir, "*.dll"))
            {
                var assemblyModified = false;
                var rp = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = true };
                rp.ReadingMode = ReadingMode.Immediate;
                rp.ReadWrite = true;
                rp.InMemory = true;
                using (var assembly = AssemblyDefinition.ReadAssembly(assemblyFile, rp))
                {
                    foreach (var module in assembly.Modules)
                    {
                        //NOTE: ToArray() so foreach does not get InvalidOperationException
                        foreach (EmbeddedResource resource in module.Resources.ToArray())
                        {
                            if (resource.Name == "__AndroidNativeLibraries__.zip")
                            {
                                var data = resource.GetResourceData();
                                using (var resourceStream = new MemoryStream(data))
                                {
                                    using (var zip = new ZipArchive(resourceStream))
                                    {
                                        foreach (var entry in zip.Entries)
                                        {
                                            //Skip directories
                                            if (string.IsNullOrEmpty(entry.Name))
                                                continue;

                                            var fileName = entry.Name;
                                            var abi = Path.GetFileName(Path.GetDirectoryName(entry.FullName));

                                            using (var zipStream = entry.Open())
                                            using (var fileStream = File.Create(Path.Combine(jniDir, abi, fileName)))
                                            {
                                                zipStream.CopyTo(fileStream);
                                            }
                                        }
                                    }
                                }

                                module.Resources.Remove(resource);
                                assemblyModified = true;
                            }
                            else if (resource.Name == "__AndroidLibraryProjects__.zip")
                            {
                                module.Resources.Remove(resource);
                                assemblyModified = true;
                            }
                        }
                    }

                    //Only write the assembly if we removed a resource
                    if (assemblyModified)
                    {
                        assembly.Write(assemblyFile);
                    }
                }
            }
        }

        /// <summary>
        /// Takes an existing JAR file and extracts it to be included withing a single JAR/AAR
        /// </summary>
        public static void ExtractJar(string jar, string classesDir, Func<ZipArchiveEntry, bool> filter = null)
        {
            using (var stream = File.OpenRead(jar))
            using (var zip = new ZipArchive(stream))
            {
                foreach (var entry in zip.Entries)
                {
                    //Skip META-INF
                    if (entry.FullName.StartsWith("META-INF", StringComparison.Ordinal))
                        continue;
                    //Filter to optionally skip
                    if (filter != null && !filter(entry))
                        continue;

                    var entryPath = Path.Combine(classesDir, entry.FullName);

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        if (!Directory.Exists(entryPath))
                            Directory.CreateDirectory(entryPath);
                    }
                    else
                    {
                        //NOTE: not all JAR files have directory entries such as FormsViewGroup.jar
                        var directoryPath = Path.GetDirectoryName(entryPath);
                        if (!Directory.Exists(directoryPath))
                            Directory.CreateDirectory(directoryPath);

                        using (var zipEntryStream = entry.Open())
                        using (var fileStream = File.Create(entryPath))
                        {
                            zipEntryStream.CopyTo(fileStream);
                        }
                    }
                }
            }
        }
    }
}