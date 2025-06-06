module Tests

open DotnetProjInfo.TestAssets
open Expecto
open Expecto.Logging
open Expecto.Logging.Message
open FileUtils
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open Ionide.ProjInfo
open Ionide.ProjInfo.Types
open Medallion.Shell
open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Xml.Linq
open System.Linq

#nowarn "25"

let RepoDir =
    (__SOURCE_DIRECTORY__
     / ".."
     / "..")
    |> Path.GetFullPath

let ExamplesDir =
    RepoDir
    / "test"
    / "examples"

let pathForTestAssets (test: TestAssetProjInfo) =
    ExamplesDir
    / test.ProjDir

let pathForProject (test: TestAssetProjInfo) =
    pathForTestAssets test
    / test.ProjectFile

let implAssemblyForProject (test: TestAssetProjInfo) = $"{test.AssemblyName}.dll"

let refAssemblyForProject (test: TestAssetProjInfo) =
    Path.Combine("ref", implAssemblyForProject test)

let getResult (r: Result<_, _>) =
    match r with
    | Ok x -> x
    | Result.Error e -> failwithf "%A" e

let TestRunDir =
    RepoDir
    / "test"
    / "testrun_ws"

let TestRunInvariantDir =
    TestRunDir
    / "invariant"

let checkExitCodeZero (cmd: Command) =
    Expect.equal 0 cmd.Result.ExitCode "command finished with exit code non-zero."

let findByPath path parsed =
    parsed
    |> Array.tryPick (fun (kv: KeyValuePair<string, ProjectOptions>) ->
        if kv.Key = path then
            Some kv
        else
            None
    )
    |> function
        | Some x -> x
        | None ->
            failwithf
                "key '%s' not found in %A"
                path
                (parsed
                 |> Array.map (fun kv -> kv.Key))

let expectFind projPath msg (parsed: ProjectOptions list) =
    let p =
        parsed
        |> List.tryFind (fun n -> n.ProjectFileName = projPath)

    Expect.isSome p msg
    p.Value


let inDir (fs: FileUtils) dirName =
    let outDir =
        TestRunDir
        / dirName

    fs.rm_rf outDir
    fs.mkdir_p outDir
    fs.cd outDir
    outDir

let copyDirFromAssets (fs: FileUtils) source outDir =
    fs.mkdir_p outDir

    let path =
        ExamplesDir
        / source

    fs.cp_r path outDir
    ()

let dotnet (fs: FileUtils) args = fs.shellExecRun "dotnet" args

let withLog name f test =
    test
        name
        (fun () ->

            let logger = Log.create (sprintf "Test '%s'" name)
            let fs = FileUtils(logger)
            f logger fs
        )

let renderOf sampleProj sources = {
    ProjectViewerTree.Name =
        sampleProj.ProjectFile
        |> Path.GetFileNameWithoutExtension
    Items =
        sources
        |> List.map (fun (path, link) -> ProjectViewerItem.Compile(path, { ProjectViewerItemConfig.Link = link }))
}

let createFCS () =
    let checker = FSharpChecker.Create(projectCacheSize = 200, keepAllBackgroundResolutions = true, keepAssemblyContents = true)
    checker

let sleepABit () =
    // CI has apparent occasional slowness
    System.Threading.Thread.Sleep 5000

[<AutoOpen>]
module ExpectNotification =

    let loading (name: string) =
        let isLoading n =
            match n with
            | WorkspaceProjectState.Loading(path) when path.EndsWith(name) -> true
            | _ -> false

        sprintf "loading %s" name, isLoading

    let loaded (name: string) =
        let isLoaded n =
            match n with
            | WorkspaceProjectState.Loaded(po, _, _) when po.ProjectFileName.EndsWith(name) -> true
            | _ -> false

        sprintf "loaded %s" name, isLoaded

    let failed (name: string) =
        let isFailed n =
            match n with
            | WorkspaceProjectState.Failed(path, _) when path.EndsWith(name) -> true
            | _ -> false

        sprintf "failed %s" name, isFailed

    let expectNotifications actual expected =

        let getMessages =
            function
            | WorkspaceProjectState.Loading(path) -> sprintf "loading %s " path
            | WorkspaceProjectState.Loaded(po, _, _) -> sprintf "loaded %s" po.ProjectFileName
            | WorkspaceProjectState.Failed(path, _) -> sprintf "failed %s" path

        Expect.equal
            (List.length actual)
            (List.length expected)
            (sprintf
                "expected notifications: %A \n actual notifications: - %A"
                (expected
                 |> List.map fst)
                (actual
                 |> List.map getMessages))

        expected
        |> List.iter (fun (name, f) ->
            let item =
                actual
                |> List.tryFind (fun a -> f a)

            let minimal_info =
                item
                |> Option.map getMessages
                |> Option.defaultValue ""

            Expect.isSome item (sprintf "expected %s but was %s" name minimal_info)
        )


    type NotificationWatcher(loader: Ionide.ProjInfo.IWorkspaceLoader, log) =
        let notifications = List<_>()

        do
            loader.Notifications.Add(fun arg ->
                notifications.Add(arg)
                log arg
            )

        member _.Notifications =
            notifications
            |> List.ofSeq

    let logNotification (logger: Logger) arg =
        logger.debug (
            eventX "notified: {notification}'"
            >> setField "notification" arg
        )

    let watchNotifications logger loader =
        NotificationWatcher(loader, logNotification logger)


let testLegacyFrameworkProject toolsPath workspaceLoader isRelease (workspaceFactory: ToolsPath * (string * string) list -> IWorkspaceLoader) =
    ptestCase
    |> withLog
        (sprintf "can load legacy project - %s - isRelease is %b" workspaceLoader isRelease)
        (fun logger fs ->

            let testDir = inDir fs "a"
            copyDirFromAssets fs ``sample7 legacy framework project``.ProjDir testDir

            let projPath =
                testDir
                / (``sample7 legacy framework project``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            let config =
                if isRelease then
                    "Release"
                else
                    "Debug"

            let props = [ ("Configuration", config) ]
            let loader = workspaceFactory (toolsPath, props)

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading projPath
                loaded projPath
            ]
            |> expectNotifications watcher.Notifications

            let [ _; WorkspaceProjectState.Loaded(n1Loaded, _, _) ] = watcher.Notifications

            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let expectedSources =
                [
                    projDir
                    / "Project1A.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal parsed.Length 1 "console and lib"
            Expect.equal n1Parsed n1Loaded "notificaton and parsed should be the same"
            Expect.equal n1Parsed.SourceFiles expectedSources "check sources"
        )

let testLegacyFrameworkMultiProject toolsPath workspaceLoader isRelease (workspaceFactory: ToolsPath * (string * string) list -> IWorkspaceLoader) =
    ptestCase
    |> withLog
        (sprintf "can load legacy project - %s - isRelease is %b" workspaceLoader isRelease)
        (fun logger fs ->

            let testDir = inDir fs "load_sample7"
            copyDirFromAssets fs ``sample7 legacy framework multi-project``.ProjDir testDir

            let projPath =
                testDir
                / (``sample7 legacy framework multi-project``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            let [ (l1, l1Dir); (l2, l2Dir) ] =
                ``sample7 legacy framework multi-project``.ProjectReferences
                |> List.map (fun p2p ->
                    testDir
                    / p2p.ProjectFile
                )
                |> List.map Path.GetFullPath
                |> List.map (fun path -> path, Path.GetDirectoryName(path))

            let config =
                if isRelease then
                    "Release"
                else
                    "Debug"

            let props = [ ("Configuration", config) ]
            let loader = workspaceFactory (toolsPath, props)

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading projPath
                loading l1
                loaded l1
                loading l2
                loaded l2
                loaded projPath
            ]
            |> expectNotifications watcher.Notifications

            let [ _; _; WorkspaceProjectState.Loaded(l1Loaded, _, _); _; WorkspaceProjectState.Loaded(l2Loaded, _, _); WorkspaceProjectState.Loaded(n1Loaded, _, _) ] =
                watcher.Notifications

            let n1Parsed =
                parsed
                |> expectFind projPath "first is a multi-project"

            let n1ExpectedSources =
                [
                    projDir
                    / "MultiProject1.fs"
                ]
                |> List.map Path.GetFullPath

            let l1Parsed =
                parsed
                |> expectFind l1 "the F# lib"

            let l1ExpectedSources =
                [
                    l1Dir
                    / "Project1A.fs"
                ]
                |> List.map Path.GetFullPath

            let l2Parsed =
                parsed
                |> expectFind l2 "the F# exe"

            let l2ExpectedSources =
                [
                    l2Dir
                    / "Project1B.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal parsed.Length 3 "check whether all projects in the multi-project were loaded"
            Expect.equal n1Parsed.SourceFiles n1ExpectedSources "check sources - N1"
            Expect.equal l1Parsed.SourceFiles l1ExpectedSources "check sources - L1"
            Expect.equal l2Parsed.SourceFiles l2ExpectedSources "check sources - L2"

            Expect.equal l1Parsed l1Loaded "l1 notificaton and parsed should be the same"
            Expect.equal l2Parsed l2Loaded "l2 notificaton and parsed should be the same"
            Expect.equal n1Parsed n1Loaded "n1 notificaton and parsed should be the same"
        )

let testSample2 toolsPath workspaceLoader isRelease (workspaceFactory: ToolsPath * (string * string) list -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample2 - isRelease is %b - %s" isRelease workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample2"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let config =
                if isRelease then
                    "Release"
                else
                    "Debug"

            let props = [ ("Configuration", config) ]
            let loader = workspaceFactory (toolsPath, props)

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading "n1.fsproj"
                loaded "n1.fsproj"
            ]
            |> expectNotifications (watcher.Notifications)

            let [ _; WorkspaceProjectState.Loaded(n1Loaded, _, _) ] = watcher.Notifications

            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let expectedSources =
                [
                    projDir
                    / ("obj/"
                       + config
                       + "/netstandard2.0/n1.AssemblyInfo.fs")
                    projDir
                    / ("obj/"
                       + config
                       + "/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs")
                    projDir
                    / "Library.fs"
                    if isRelease then
                        projDir
                        / "Other.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal parsed.Length 1 "console and lib"
            Expect.equal n1Parsed n1Loaded "notificaton and parsed should be the same"
            Expect.equal n1Parsed.SourceFiles expectedSources "check sources"
        )

let testSample3 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) expected =
    testCase
    |> withLog
        (sprintf "can load sample3 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample3"
            copyDirFromAssets fs ``sample3 Netsdk projs``.ProjDir testDir

            let projPath =
                testDir
                / (``sample3 Netsdk projs``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            let [ (l1, l1Dir); (l2, l2Dir) ] =
                ``sample3 Netsdk projs``.ProjectReferences
                |> List.map (fun p2p ->
                    testDir
                    / p2p.ProjectFile
                )
                |> List.map Path.GetFullPath
                |> List.map (fun path -> path, Path.GetDirectoryName(path))

            dotnet fs [
                "build"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            expected
            |> expectNotifications (watcher.Notifications)


            let [ _; _; WorkspaceProjectState.Loaded(l1Loaded, _, _); _; WorkspaceProjectState.Loaded(l2Loaded, _, _); WorkspaceProjectState.Loaded(c1Loaded, _, _) ] =
                watcher.Notifications


            let l1Parsed =
                parsed
                |> expectFind l1 "the C# lib"

            let l1ExpectedSources =
                [
                    l1Dir
                    / "Class1.cs"
                    l1Dir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.cs"
                    l1Dir
                    / "obj/Debug/netstandard2.0/l1.AssemblyInfo.cs"
                ]
                |> List.map Path.GetFullPath

            // TODO C# doesnt have OtherOptions or SourceFiles atm. it should
            // Expect.equal l1Parsed.SourceFiles l1ExpectedSources "check sources"

            let l2Parsed =
                parsed
                |> expectFind l2 "the F# lib"

            let l2ExpectedSources =
                [
                    l2Dir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs"
                    l2Dir
                    / "obj/Debug/netstandard2.0/l2.AssemblyInfo.fs"
                    l2Dir
                    / "Library.fs"
                ]
                |> List.map Path.GetFullPath


            let c1Parsed =
                parsed
                |> expectFind projPath "the F# console"


            let c1ExpectedSources =
                [
                    projDir
                    / "obj/Debug/netcoreapp2.1/.NETCoreApp,Version=v2.1.AssemblyAttributes.fs"
                    projDir
                    / "obj/Debug/netcoreapp2.1/c1.AssemblyInfo.fs"
                    projDir
                    / "Program.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal
                parsed.Length
                3
                (sprintf
                    "console (F#) and lib (F#) and lib (C#), but was %A"
                    (parsed
                     |> List.map (fun x -> x.ProjectFileName)))

            Expect.equal c1Parsed.SourceFiles c1ExpectedSources "check sources - C1"
            Expect.equal l1Parsed.SourceFiles l1ExpectedSources "check sources - L1"
            Expect.equal l2Parsed.SourceFiles l2ExpectedSources "check sources - L2"

            Expect.equal l1Parsed l1Loaded "l1 notificaton and parsed should be the same"
            Expect.equal l2Parsed l2Loaded "l2 notificaton and parsed should be the same"
            Expect.equal c1Parsed c1Loaded "c1 notificaton and parsed should be the same"
        )

let testSample4 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample4 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample4"
            copyDirFromAssets fs ``sample4 NetSdk multi tfm``.ProjDir testDir

            let projPath =
                testDir
                / (``sample4 NetSdk multi tfm``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading "m1.fsproj"
                loaded "m1.fsproj"
            ]
            |> expectNotifications (watcher.Notifications)

            let [ _; WorkspaceProjectState.Loaded(m1Loaded, _, _) ] = watcher.Notifications


            Expect.equal
                parsed.Length
                1
                (sprintf
                    "multi-tfm lib (F#), but was %A"
                    (parsed
                     |> List.map (fun x -> x.ProjectFileName)))

            let m1Parsed =
                parsed
                |> expectFind projPath "the F# console"

            let m1ExpectedSources =
                [
                    projDir
                    / "obj/Debug/netstandard2.0/m1.AssemblyInfo.fs"
                    projDir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs"
                    projDir
                    / "LibraryA.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal m1Parsed.SourceFiles m1ExpectedSources "check sources"
            Expect.equal m1Parsed m1Loaded "m1 notificaton and parsed should be the same"
        )

let expectEqualSourcesIgnoreOrder (actual: string seq) (expected: string seq) =
    Expect.equal
        (actual
         |> Seq.sort
         |> Seq.toArray)
        (expected
         |> Seq.sort
         |> Seq.toArray)

let testSample5 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample5 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample5"
            copyDirFromAssets fs ``sample5 NetSdk CSharp library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample5 NetSdk CSharp library``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading "l2.csproj"
                loaded "l2.csproj"
            ]
            |> expectNotifications (watcher.Notifications)

            let [ _; WorkspaceProjectState.Loaded(l2Loaded, _, _) ] = watcher.Notifications


            Expect.equal parsed.Length 1 "lib"

            let l2Parsed =
                parsed
                |> expectFind projPath "a C# lib"

            let l2ExpectedSources =
                [
                    projDir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.cs"
                    projDir
                    / "Class1.cs"
                    projDir
                    / "obj/Debug/netstandard2.0/l2.AssemblyInfo.cs"
                ]
                |> List.map Path.GetFullPath

            // TODO C# doesnt have OtherOptions atm. It should.
            expectEqualSourcesIgnoreOrder l2Parsed.SourceFiles l2ExpectedSources "check sources"

            Expect.equal l2Parsed l2Loaded "l2 notificaton and parsed should be the same"
        )

let testLoadSln toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) expected =
    testCase
    |> withLog
        (sprintf "can load sln - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sln"
            copyDirFromAssets fs ``sample6 Netsdk Sparse/sln``.ProjDir testDir

            let slnPath =
                testDir
                / (``sample6 Netsdk Sparse/sln``.ProjectFile)

            dotnet fs [
                "restore"
                slnPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadSln(slnPath)
                |> Seq.toList


            expected
            |> expectNotifications (watcher.Notifications)


            Expect.equal parsed.Length 3 "c1, l1, l2"

            let c1 =
                testDir
                / (``sample6 Netsdk Sparse/1``.ProjectFile)

            let c1Dir = Path.GetDirectoryName c1

            let [ l2 ] =
                ``sample6 Netsdk Sparse/1``.ProjectReferences
                |> List.map (fun p2p ->
                    testDir
                    / p2p.ProjectFile
                )

            let l2Dir = Path.GetDirectoryName l2

            let l1 =
                testDir
                / (``sample6 Netsdk Sparse/2``.ProjectFile)

            let l1Dir = Path.GetDirectoryName l1

            let l1Parsed =
                parsed
                |> expectFind l1 "the F# lib"

            let l1ExpectedSources =
                [
                    l1Dir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs"
                    l1Dir
                    / "obj/Debug/netstandard2.0/l1.AssemblyInfo.fs"
                    l1Dir
                    / "Library.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal l1Parsed.SourceFiles l1ExpectedSources "check sources l1"
            Expect.equal l1Parsed.ReferencedProjects [] "check p2p l1"

            let l2Parsed =
                parsed
                |> expectFind l2 "the C# lib"

            let l2ExpectedSources =
                [
                    l2Dir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs"
                    l2Dir
                    / "obj/Debug/netstandard2.0/l2.AssemblyInfo.fs"
                    l2Dir
                    / "Library.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal l2Parsed.SourceFiles l2ExpectedSources "check sources l2"
            Expect.equal l2Parsed.ReferencedProjects [] "check p2p l2"

            let c1Parsed =
                parsed
                |> expectFind c1 "the F# console"

            let c1ExpectedSources =
                [
                    c1Dir
                    / "obj/Debug/netcoreapp2.1/.NETCoreApp,Version=v2.1.AssemblyAttributes.fs"
                    c1Dir
                    / "obj/Debug/netcoreapp2.1/c1.AssemblyInfo.fs"
                    c1Dir
                    / "Program.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal c1Parsed.SourceFiles c1ExpectedSources "check sources c1"
            Expect.equal c1Parsed.ReferencedProjects.Length 1 "check p2p c1"

        )

let testParseSln toolsPath =
    testCase
    |> withLog
        "can parse sln"
        (fun logger fs ->
            let testDir = inDir fs "parse_sln"
            copyDirFromAssets fs ``sample6 Netsdk Sparse/sln``.ProjDir testDir

            let slnPath =
                testDir
                / (``sample6 Netsdk Sparse/sln``.ProjectFile)

            dotnet fs [
                "restore"
                slnPath
            ]
            |> checkExitCodeZero

            let p = InspectSln.tryParseSln (slnPath)

            Expect.isTrue
                (match p with
                 | Ok _ -> true
                 | Result.Error _ -> false)
                "expected successful parse"

            let actualProjects =
                InspectSln.loadingBuildOrder (
                    match p with
                    | Ok(data) -> data
                    | _ -> failwith "unreachable"
                )
                |> List.map Path.GetFullPath

            let expectedProjects =
                [
                    Path.Combine(testDir, "c1", "c1.fsproj")
                    Path.Combine(testDir, "l1", "l1.fsproj")
                    Path.Combine(testDir, "l2", "l2.fsproj")
                ]
                |> List.map Path.GetFullPath

            Expect.equal actualProjects expectedProjects "expected successful calculation of loading build order of solution"

        )

let testSample9 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample9 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample9"
            copyDirFromAssets fs ``sample9 NetSdk library``.ProjDir testDir
            // fs.cp (``sample9 NetSdk library``.ProjDir/"Directory.Build.props") testDir

            let projPath =
                testDir
                / (``sample9 NetSdk library``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading "n1.fsproj"
                loaded "n1.fsproj"
            ]
            |> expectNotifications (watcher.Notifications)

            let [ _; WorkspaceProjectState.Loaded(n1Loaded, _, _) ] = watcher.Notifications


            Expect.equal parsed.Length 1 "console and lib"

            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let expectedSources =
                [
                    projDir
                    / "obj2/Debug/netstandard2.0/n1.AssemblyInfo.fs"
                    projDir
                    / "obj2/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs"
                    projDir
                    / "Library.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal n1Parsed.SourceFiles expectedSources "check sources"

            Expect.equal n1Parsed n1Loaded "notificaton and parsed should be the same"
        )

let testSample10 toolsPath workspaceLoader isRelease (workspaceFactory: ToolsPath * (string * string) list -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample10 - %s - isRelease is %b" workspaceLoader isRelease)
        (fun logger fs ->
            let testDir = inDir fs "load_sample10"
            copyDirFromAssets fs ``sample10 NetSdk library with custom targets``.ProjDir testDir

            let projPath =
                testDir
                / (``sample10 NetSdk library with custom targets``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let config =
                if isRelease then
                    "Release"
                else
                    "Debug"

            let props = [ ("Configuration", config) ]
            let loader = workspaceFactory (toolsPath, props)

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            [
                loading "n1.fsproj"
                loaded "n1.fsproj"
            ]
            |> expectNotifications (watcher.Notifications)

            let [ _; WorkspaceProjectState.Loaded(n1Loaded, _, _) ] = watcher.Notifications

            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let expectedSources =
                [
                    projDir
                    / ("obj/"
                       + config
                       + "/netstandard2.0/n1.AssemblyInfo.fs")
                    projDir
                    / ("obj/"
                       + config
                       + "/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs")
                    projDir
                    / "BeforeBuild.fs"
                    projDir
                    / "BeforeCompile.fs"
                ]
                |> List.map Path.GetFullPath

            Expect.equal parsed.Length 1 "lib"
            Expect.equal n1Parsed n1Loaded "notificaton and parsed should be the same"
            Expect.equal n1Parsed.SourceFiles expectedSources "check sources"
        )

let testRender2 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can render sample2 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "render_sample2"
            let sampleProj = ``sample2 NetSdk library``
            copyDirFromAssets fs sampleProj.ProjDir testDir

            let projPath =
                testDir
                / (sampleProj.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList


            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let rendered = ProjectViewer.render n1Parsed

            let expectedSources =
                [
                    projDir
                    / "Library.fs",
                    "Library.fs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            Expect.equal rendered (renderOf sampleProj expectedSources) "check rendered project"
        )

let testRender3 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can render sample3 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "render_sample3"
            let c1Proj = ``sample3 Netsdk projs``
            copyDirFromAssets fs c1Proj.ProjDir testDir

            let projPath =
                testDir
                / (c1Proj.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            let [ (l1Proj, l1, l1Dir); (l2Proj, l2, l2Dir) ] =
                c1Proj.ProjectReferences
                |> List.map (fun p2p ->
                    p2p,
                    Path.GetFullPath(
                        testDir
                        / p2p.ProjectFile
                    )
                )
                |> List.map (fun (p2p, path) -> p2p, path, Path.GetDirectoryName(path))

            dotnet fs [
                "build"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let projDir = Path.GetDirectoryName projPath

            let parsed =
                loader.LoadProjects([ projPath ], [], BinaryLogGeneration.Within(DirectoryInfo projDir))
                |> Seq.toList

            let l1Parsed =
                parsed
                |> expectFind l1 "the C# lib"

            let l2Parsed =
                parsed
                |> expectFind l2 "the F# lib"

            let c1Parsed =
                parsed
                |> expectFind projPath "the F# console"


            let l1ExpectedSources =
                [
                    l1Dir
                    / "Class1.cs",
                    "Class1.cs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            Expect.equal (ProjectViewer.render l1Parsed) (renderOf l1Proj l1ExpectedSources) "check rendered l1"

            let l2ExpectedSources =
                [
                    l2Dir
                    / "Library.fs",
                    "Library.fs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            Expect.equal (ProjectViewer.render l2Parsed) (renderOf l2Proj l2ExpectedSources) "check rendered l2"


            let c1ExpectedSources =
                [
                    projDir
                    / "Program.fs",
                    "Program.fs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            Expect.equal (ProjectViewer.render c1Parsed) (renderOf c1Proj c1ExpectedSources) "check rendered c1"
        )

let testRender4 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can render sample4 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "render_sample4"
            let m1Proj = ``sample4 NetSdk multi tfm``
            copyDirFromAssets fs m1Proj.ProjDir testDir

            let projPath =
                testDir
                / (m1Proj.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            let m1Parsed =
                parsed
                |> expectFind projPath "the F# console"

            let m1ExpectedSources =
                [
                    projDir
                    / "LibraryA.fs",
                    "LibraryA.fs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            Expect.equal (ProjectViewer.render m1Parsed) (renderOf m1Proj m1ExpectedSources) "check rendered m1"
        )

let testRender5 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can render sample5 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "render_sample5"
            let l2Proj = ``sample5 NetSdk CSharp library``
            copyDirFromAssets fs l2Proj.ProjDir testDir

            let projPath =
                testDir
                / (l2Proj.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList


            let l2Parsed =
                parsed
                |> expectFind projPath "a C# lib"

            let l2ExpectedSources =
                [
                    projDir
                    / "Class1.cs",
                    "Class1.cs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            // TODO C# doesnt have OtherOptions or SourceFiles atm. it should
            Expect.equal (ProjectViewer.render l2Parsed) (renderOf l2Proj l2ExpectedSources) "check rendered l2"
        )

let testRender8 toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can render sample8 - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "render_sample8"
            let sampleProj = ``sample8 NetSdk Explorer``
            copyDirFromAssets fs sampleProj.ProjDir testDir

            let projPath =
                testDir
                / (sampleProj.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList


            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let rendered = ProjectViewer.render n1Parsed

            let expectedSources =
                [
                    projDir
                    / "LibraryA.fs",
                    "Component/TheLibraryA.fs"
                    projDir
                    / "LibraryC.fs",
                    "LibraryC.fs"
                    projDir
                    / "LibraryB.fs",
                    "Component/Auth/TheLibraryB.fs"
                ]
                |> List.map (fun (p, l) -> Path.GetFullPath p, l)

            Expect.equal rendered (renderOf sampleProj expectedSources) "check rendered project"
        )

let testProjectNotFound toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "project not found - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "proj_not_found"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let wrongPath =
                let dir, name, ext = Path.GetDirectoryName projPath, Path.GetFileNameWithoutExtension projPath, Path.GetExtension projPath

                Path.Combine(
                    dir,
                    name
                    + "aa"
                    + ext
                )

            let parsed =
                loader.LoadProjects [ wrongPath ]
                |> Seq.toList


            [
                ExpectNotification.loading "n1aa.fsproj"
                ExpectNotification.failed "n1aa.fsproj"
            ]
            |> expectNotifications (watcher.Notifications)


            Expect.equal parsed.Length 0 "no project loaded"

            Expect.equal
                (watcher.Notifications
                 |> List.item 1)
                (WorkspaceProjectState.Failed(wrongPath, (GetProjectOptionsErrors.ProjectNotFound(wrongPath))))
                "check error type"
        )

let testFCSmap toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample2 with FCS - %s" workspaceLoader)
        (fun logger fs ->

            let rec allFCSProjects (po: FSharpProjectOptions) = [
                yield po
                for reference in po.ReferencedProjects do
                    match reference with
                    | FSharpReferencedProject.FSharpReference(options = options) -> yield! allFCSProjects options
                    | _ -> ()
            ]


            let rec allP2P (po: FSharpProjectOptions) = [
                for reference in po.ReferencedProjects do
                    match reference with
                    | FSharpReferencedProject.FSharpReference(outputFile, options) ->
                        yield (outputFile, options)
                        yield! allP2P options
                    | _ -> ()
            ]

            let expectP2PKeyIsTargetPath (pos: Map<string, ProjectOptions>) fcsPo =
                for (tar, fcsPO) in allP2P fcsPo do
                    let dpoPo =
                        pos
                        |> Map.find fcsPo.ProjectFileName

                    Expect.equal tar dpoPo.TargetPath (sprintf "p2p key is TargetPath, fsc projet options was '%A'" fcsPO)

            let testDir = inDir fs "load_sample_fsc"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            let mutable pos = Map.empty

            loader.Notifications.Add(
                function
                | WorkspaceProjectState.Loaded(po, knownProjects, _) -> pos <- Map.add po.ProjectFileName po pos
            )

            let fcsPo = FCS.mapToFSharpProjectOptions parsed.Head parsed

            let po =
                parsed
                |> expectFind projPath "first is a lib"

            Expect.equal fcsPo.LoadTime po.LoadTime "load time"

            Expect.equal fcsPo.ReferencedProjects.Length ``sample2 NetSdk library``.ProjectReferences.Length "refs"

            //TODO check fullpaths
            Expect.equal
                fcsPo.SourceFiles
                (po.SourceFiles
                 |> Array.ofList)
                "check sources"

            expectP2PKeyIsTargetPath pos fcsPo

            let fcs = createFCS ()

            let result =
                fcs.ParseAndCheckProject(fcsPo)
                |> Async.RunSynchronously

            Expect.isEmpty result.Diagnostics (sprintf "no errors but was: %A" result.Diagnostics)

            let uses = result.GetAllUsesOfAllSymbols()

            Expect.isNonEmpty uses "all symbols usages"

        )

let testFCSmapManyProj toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    ptestCase
    |> withLog
        (sprintf "can load sample3 with FCS - %s" workspaceLoader)
        (fun logger fs ->

            let rec allFCSProjects (po: FSharpProjectOptions) = [
                yield po
                for reference in po.ReferencedProjects do
                    match reference with
                    | FSharpReferencedProject.FSharpReference(options = opts) -> yield! allFCSProjects opts
                    | _ -> ()
            ]

            let rec allP2P (po: FSharpProjectOptions) = [
                for reference in po.ReferencedProjects do
                    match reference with
                    | FSharpReferencedProject.FSharpReference(outputFile, opts) ->
                        yield outputFile, opts
                        yield! allP2P opts
                    | _ -> ()
            ]

            let testDir = inDir fs "load_sample_fsc"
            copyDirFromAssets fs ``sample3 Netsdk projs``.ProjDir testDir

            let projPath =
                testDir
                / (``sample3 Netsdk projs``.ProjectFile)

            // Build csproj, so it should be referenced as FSharpReferencedProject.PortableExecutable
            let csproj =
                testDir
                / ``sample3 Netsdk projs``.ProjectReferences.[0].ProjectFile

            dotnet fs [
                "build"
                csproj
            ]
            |> checkExitCodeZero

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            let mutable pos = Map.empty

            loader.Notifications.Add(
                function
                | WorkspaceProjectState.Loaded(po, knownProjects, _) -> pos <- Map.add po.ProjectFileName po pos
            )

            let parsedC1 =
                parsed
                |> Seq.find (fun x -> x.ProjectFileName.EndsWith(``sample3 Netsdk projs``.ProjectFile))

            logger.info (
                Message.eventX "Has the following parsed projects: {map}"
                >> Message.setField "map" pos
            )

            let fcsPo = FCS.mapToFSharpProjectOptions parsedC1 parsed

            let references =
                fcsPo.OtherOptions
                |> Seq.choose (fun r ->
                    if r.StartsWith "-r:" then
                        System.IO.Path.GetFileName(r.[3..])
                        |> Some
                    else
                        None
                )

            let projectReferences =
                fcsPo.ReferencedProjects
                |> Seq.map (fun r ->
                    r.OutputFile
                    |> System.IO.Path.GetFileName
                )

            Expect.contains references "l1.dll" "Should have direct dll reference to C# reference"
            Expect.contains projectReferences "l1.dll" "Should have project reference to C# reference"
            Expect.contains references "l2.dll" "Should have direct dll reference to F# reference"
            Expect.contains projectReferences "l2.dll" "Should have project reference to F# reference"
        )

let countDistinctObjectsByReference<'a> (items: 'a seq) =
    let set =
        HashSet(
            items
            |> Seq.map (fun i -> i :> obj),
            ReferenceEqualityComparer.Instance
        )

    set.Count

let testFCSmapManyProjCheckCaching =
    testCase
    |> withLog
        "When creating FCS options, caches them"
        (fun _ _ ->

            let sdkInfo = ProjectLoader.getSdkInfo []

            let template: ProjectOptions = {
                ProjectId = None
                ProjectFileName = "Template"
                TargetFramework = "TF"
                SourceFiles = []
                OtherOptions = []
                ReferencedProjects = []
                PackageReferences = []
                LoadTime = DateTime.MinValue
                TargetPath = "TP"
                TargetRefPath = Some "TRP"
                ProjectOutputType = ProjectOutputType.Library
                ProjectSdkInfo = sdkInfo
                Items = []
                Properties = []
                CustomProperties = []
            }

            let makeReference (options: ProjectOptions) = {
                RelativePath = options.ProjectFileName
                ProjectFileName = options.ProjectFileName
                TargetFramework = options.TargetFramework
            }

            let makeProject (name: string) (referencedProjects: ProjectOptions list) = {
                template with
                    ProjectFileName = name
                    ReferencedProjects =
                        referencedProjects
                        |> List.map makeReference
            }

            let projectsInLayers =
                let layerCount = 4
                let layerSize = 2

                let layers =
                    [ 1..layerCount ]
                    |> List.map (fun layer ->
                        [ 1..layerSize ]
                        |> List.map (fun item -> makeProject $"layer{layer}_{item}.fsproj" [])
                    )

                let first = layers[0]

                let rest =
                    layers
                    |> List.pairwise
                    |> List.map (fun (previous, next) ->
                        next
                        |> List.map (fun p -> {
                            p with
                                ReferencedProjects =
                                    previous
                                    |> List.map makeReference
                        })
                    )

                let layers =
                    first
                    :: rest

                layers
                |> List.concat

            let fcsOptions = FCS.mapManyOptions projectsInLayers

            let rec findProjectOptionsTransitively (project: FSharpProjectOptions) =
                project.ReferencedProjects
                |> Array.toList
                |> List.collect (fun reference ->
                    match reference with
                    | FSharpReferencedProject.FSharpReference(_, options) -> findProjectOptionsTransitively options
                    | _ -> []
                )
                |> List.append [ project ]

            let findDistinctProjectOptionsTransitively (projects: FSharpProjectOptions seq) =
                projects
                |> Seq.collect findProjectOptionsTransitively
                |> countDistinctObjectsByReference

            let distinctOptionsCount = findDistinctProjectOptionsTransitively fcsOptions

            Expect.equal distinctOptionsCount projectsInLayers.Length "Mapping should reuse instances of FSharpProjectOptions and only create one per project"
        )

let testSample2WithBinLog binLogFile toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        (sprintf "can load sample2 with bin log - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample2_bin_log"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            let projDir = Path.GetDirectoryName projPath

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let watcher = watchNotifications logger loader

            let parsed =
                loader.LoadProjects([ projPath ], [], BinaryLogGeneration.Within(DirectoryInfo projDir))
                |> Seq.toList

            [
                loading "n1.fsproj"
                loaded "n1.fsproj"
            ]
            |> expectNotifications (watcher.Notifications)

            let [ _; WorkspaceProjectState.Loaded(n1Loaded, _, _) ] = watcher.Notifications

            let n1Parsed =
                parsed
                |> expectFind projPath "first is a lib"

            let expectedSources =
                [
                    projDir
                    / "obj/Debug/netstandard2.0/n1.AssemblyInfo.fs"
                    projDir
                    / "obj/Debug/netstandard2.0/.NETStandard,Version=v2.0.AssemblyAttributes.fs"
                    projDir
                    / "Library.fs"
                ]
                |> List.map Path.GetFullPath

            let blPath =
                projDir
                / binLogFile

            let blExists = File.Exists blPath

            Expect.isTrue blExists "binlog file should exist"
            Expect.equal parsed.Length 1 "console and lib"
            Expect.equal n1Parsed n1Loaded "notificaton and parsed should be the same"
            Expect.equal n1Parsed.SourceFiles expectedSources "check sources"
        )

[<AutoOpen>]
module ExpectProjectSystemNotification =

    open Ionide.ProjInfo.ProjectSystem

    let loading (name: string) =
        let isLoading n =
            match n with
            | ProjectResponse.ProjectLoading(path) when path.EndsWith(name) -> true
            | _ -> false

        sprintf "loading %s" name, isLoading


    let loadedFrom (name: string) isCached =
        let isLoaded n =
            match n with
            | ProjectResponse.Project(po, fromCache) when
                po.ProjectFileName.EndsWith(name)
                && fromCache = isCached
                ->
                true
            | _ -> false

        sprintf "loaded %s %b" name isCached, isLoaded

    let loaded (name: string) =
        let isLoaded n =
            match n with
            | ProjectResponse.Project(po, _) when po.ProjectFileName.EndsWith(name) -> true
            | _ -> false

        sprintf "loaded %s" name, isLoaded


    let failed (name: string) =
        let isFailed n =
            match n with
            | ProjectResponse.ProjectError(path, _) when path.EndsWith(name) -> true
            | _ -> false

        sprintf "failed %s" name, isFailed

    let workspace (status: bool) =
        let isFailed n =
            match n with
            | ProjectResponse.WorkspaceLoad(s) when s = status -> true
            | _ -> false

        sprintf "workspace %b" status, isFailed

    let changed (name: string) =
        let isFailed n =
            match n with
            | ProjectResponse.ProjectChanged(path) when path.EndsWith(name) -> true
            | _ -> false

        sprintf "changed %s" name, isFailed

    let expectNotifications actual expected =
        let getMessage =
            function
            | ProjectResponse.ProjectLoading(path) -> sprintf "loading %s" (System.IO.Path.GetFileName path)
            | ProjectResponse.Project(po, fromCache) -> sprintf "loaded %s %b" (System.IO.Path.GetFileName po.ProjectFileName) fromCache
            | ProjectResponse.ProjectError(path, _) -> sprintf "failed %s" (System.IO.Path.GetFileName path)
            | ProjectResponse.WorkspaceLoad(finished) -> sprintf "workspace %b" finished
            | ProjectResponse.ProjectChanged(projectFileName) -> sprintf "changed %s" (System.IO.Path.GetFileName projectFileName)

        Expect.equal
            (List.length actual)
            (List.length expected)
            (sprintf
                "expected notifications: %A\n actual notifications %A"
                (expected
                 |> List.map fst)
                (actual
                 |> List.map getMessage))

        expected
        |> List.zip actual
        |> List.iter (fun (n, check) ->
            let name, f = check

            let minimal_info = getMessage n


            Expect.isTrue (f n) (sprintf "expected %s but was %s" name minimal_info)
        )

    type NotificationWatcher(controller: ProjectController, log) =
        let notifications = List<_>()

        do
            controller.Notifications.Add(fun arg ->
                notifications.Add(arg)
                log arg
            )

        member _.Notifications =
            notifications
            |> List.ofSeq

    let logNotification (logger: Logger) arg =
        logger.debug (
            eventX "notified: {notification}'"
            >> setField "notification" arg
        )

    let watchNotifications logger controller =
        NotificationWatcher(controller, logNotification logger)

let testLoadProject toolsPath =
    testCase
    |> withLog
        (sprintf "can use getProjectInfo")
        (fun logger fs ->
            let testDir = inDir fs "getProjectInfo"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let collection = new Microsoft.Build.Evaluation.ProjectCollection()

            match ProjectLoader.loadProject projPath BinaryLogGeneration.Off collection with
            | Result.Error err -> failwith $"{err}"
            | Result.Ok proj ->
                match ProjectLoader.getLoadedProjectInfo projPath [] proj with
                | Ok(ProjectLoader.LoadedProjectInfo.StandardProjectInfo proj) -> Expect.equal proj.ProjectFileName projPath "project file names"
                | Ok(ProjectLoader.LoadedProjectInfo.TraversalProjectInfo refs) -> failwith "expected standard project, not a traversal project"
                | Result.Error err -> failwith $"{err}"
        )

let testProjectSystem toolsPath workspaceLoader workspaceFactory =
    testCase
    |> withLog
        (sprintf "can load sample2 with Project System - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample2_projectSystem"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero


            use controller = new ProjectSystem.ProjectController(toolsPath, workspaceFactory)
            let watcher = watchNotifications logger controller

            let result =
                controller.LoadProject(projPath)
                |> Async.RunSynchronously

            Expect.isTrue result "load succeeds"

            let parsed =
                controller.ProjectOptions
                |> Seq.toList
                |> List.map (snd)

            let fcsPo = parsed.Head

            [
                workspace false
                loading "n1.fsproj"
                loaded "n1.fsproj"
                workspace true
            ]
            |> expectNotifications (watcher.Notifications)

            Expect.equal fcsPo.ReferencedProjects.Length ``sample2 NetSdk library``.ProjectReferences.Length "refs"
            Expect.equal fcsPo.SourceFiles.Length 3 "files"

            let fcs = createFCS ()

            let result =
                fcs.ParseAndCheckProject(fcsPo)
                |> Async.RunSynchronously

            Expect.isEmpty result.Diagnostics (sprintf "no errors but was: %A" result.Diagnostics)

            let uses = result.GetAllUsesOfAllSymbols()

            Expect.isNonEmpty uses "all symbols usages"
        )

let testProjectSystemCacheLoad toolsPath workspaceLoader workspaceFactory =
    testCase
    |> withLog
        (sprintf "project system can reload from cache - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "reload_sample2_projectSystem"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            use controller = new ProjectSystem.ProjectController(toolsPath, workspaceFactory)
            let watcher = watchNotifications logger controller

            let result =
                controller.LoadProject(projPath)
                |> Async.RunSynchronously

            Expect.isTrue result "load succeeds"

            let parsed =
                controller.ProjectOptions
                |> Seq.toList
                |> List.map (snd)

            let fcsPo = parsed.Head

            [
                workspace false
                loading "n1.fsproj"
                loadedFrom "n1.fsproj" false
                workspace true
            ]
            |> expectNotifications (watcher.Notifications)


            Expect.equal fcsPo.ReferencedProjects.Length ``sample2 NetSdk library``.ProjectReferences.Length "refs"
            Expect.equal fcsPo.SourceFiles.Length 3 "files"

            let fcs = createFCS ()

            let result =
                fcs.ParseAndCheckProject(fcsPo)
                |> Async.RunSynchronously

            Expect.isEmpty result.Diagnostics (sprintf "no errors but was: %A" result.Diagnostics)

            let uses = result.GetAllUsesOfAllSymbols()

            Expect.isNonEmpty uses "all symbols usages"

            // this sucks, but we wait here for a bit until the cache is saved
            System.Threading.Thread.Sleep(2000)

            use controller = new ProjectSystem.ProjectController(toolsPath, workspaceFactory)
            let watcher2 = watchNotifications logger controller

            let result2 =
                controller.LoadProject(projPath)
                |> Async.RunSynchronously

            Expect.isTrue result2 "load succeeds"

            let parsed2 =
                controller.ProjectOptions
                |> Seq.toList
                |> List.map (snd)

            let fcsPo2 = parsed.Head

            System.Threading.Thread.Sleep(1000)

            [
                workspace false
                loading "n1.fsproj"
                loadedFrom "n1.fsproj" true
                workspace true
            ]
            |> expectNotifications (watcher2.Notifications)

            Expect.equal fcsPo2.ReferencedProjects.Length ``sample2 NetSdk library``.ProjectReferences.Length "refs"
            Expect.equal fcsPo2.SourceFiles.Length 3 "files"

            let fcs2 = createFCS ()

            let result2 =
                fcs2.ParseAndCheckProject(fcsPo2)
                |> Async.RunSynchronously

            Expect.isEmpty result2.Diagnostics (sprintf "no errors but was: %A" result2.Diagnostics)

            let uses2 = result2.GetAllUsesOfAllSymbols()

            Expect.isNonEmpty uses2 "all symbols usages"

        )

let testProjectSystemOnChange toolsPath workspaceLoader workspaceFactory =
    testCase
    |> withLog
        (sprintf "can load sample2 with Project System, detect change on fsproj - %s" workspaceLoader)
        (fun logger fs ->
            let testDir = inDir fs "load_sample2_projectSystem_onChange"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            use controller = new ProjectSystem.ProjectController(toolsPath, workspaceFactory)
            let watcher = watchNotifications logger controller

            let result =
                controller.LoadProject(projPath)
                |> Async.RunSynchronously

            Expect.isTrue result "load succeeds"

            [
                workspace false
                loading "n1.fsproj"
                loaded "n1.fsproj"
                workspace true
            ]
            |> expectNotifications (watcher.Notifications)

            fs.touch projPath

            sleepABit ()

            [
                workspace false
                loading "n1.fsproj"
                loaded "n1.fsproj"
                workspace true
                changed "n1.fsproj"
                workspace false
                loading "n1.fsproj"
                loaded "n1.fsproj"
                workspace true
            ]
            |> expectNotifications (watcher.Notifications)

        )

let debugTests toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    ptestCase
    |> withLog
        (sprintf "debug - %s" workspaceLoader)
        (fun logger fs ->

            let loader = workspaceFactory toolsPath

            let slnPath =
                @"C:\Users\JimmyByrd\Documents\Repositories\public\TheAngryByrd\FsToolkit.ErrorHandling\FsToolkit.ErrorHandling.sln"

            let parsedProjs =
                loader.LoadSln slnPath
                |> Seq.toList

            // printfn "%A" parsedProjs
            parsedProjs
            |> Seq.iter (fun p -> Expect.isGreaterThan p.SourceFiles.Length 0 $"{p.ProjectFileName} Should have SourceFiles")
        )

let expensiveTests toolsPath (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    ptest "can load project that uses workloads" {
        // this one requires a lot of setup that I didn't want to check in because it's huge.
        // before you can run this test you need to have
        // * installed the android workload: `dotnet workload install android`
        // * installed the android sdk. This seems to mostly be done from VS or Android Studio
        // then you can actually crack this project
        let projPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "examples", "sample-workload", "sample-workload.csproj")
        let loader = workspaceFactory toolsPath

        let parsed =
            loader.LoadProjects [ projPath ]
            |> Seq.toList

        let projInfo = parsed[0]

        let references =
            projInfo.OtherOptions
            |> Seq.filter (fun opt -> opt.StartsWith "-r:")

        Expect.exists
            references
            (fun r ->
                r.Contains "packs"
                && r.Contains "Microsoft.Android."
            )
            "Should have found a reference to android dlls in the packs directory"
    }

let addFileToProject (projPath: string) fileName =
    let doc = XDocument.Load(projPath)
    let df = doc.Root.Name.Namespace

    doc.Root
        .Elements(
            df
            + "ItemGroup"
        )
        .ElementAt(0)
        .Add(
            new XElement(
                df
                + "Compile",
                new XAttribute("Include", fileName)
            )
        )

    doc.Save(projPath)

let loadProjfileFromDiskTests toolsPath workspaceLoader (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        $"can load project from disk everytime - {workspaceLoader}"
        (fun logger fs ->

            let loader = workspaceFactory toolsPath
            let testDir = inDir fs "reload_sample2_from_disk"
            copyDirFromAssets fs ``sample2 NetSdk library``.ProjDir testDir

            let projPath =
                testDir
                / (``sample2 NetSdk library``.ProjectFile)

            dotnet fs [
                "restore"
                projPath
            ]
            |> checkExitCodeZero

            let result =
                loader.LoadProjects [ projPath ]
                |> Seq.head

            Expect.equal result.SourceFiles.Length 3 "Should have 2 source file"

            "Foo.fs"
            |> addFileToProject projPath

            let result =
                loader.LoadProjects [ projPath ]
                |> Seq.head

            Expect.equal result.SourceFiles.Length 4 "Should have 3 source file"
        )

let csharpLibTest toolsPath (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        "can load project that has a csharp project reference"
        (fun logger fs ->
            let projPath =
                Path.Combine(__SOURCE_DIRECTORY__, "..", "examples", "sample-referenced-csharp-project", "fsharp-exe", "fsharp-exe.fsproj")
            // need to build the projects first so that there's something to latch on to
            dotnet fs [
                "build"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            Expect.hasLength parsed 2 "Should have loaded the F# exe and the C# lib"
            let fsharpProject = parsed[0]
            let mapped = FCS.mapToFSharpProjectOptions fsharpProject parsed
            let referencedProjects = mapped.ReferencedProjects
            Expect.hasLength referencedProjects 1 "Should have a reference to the C# lib"

            match referencedProjects[0] with
            | FSharpReferencedProject.PEReference(delayedReader = reader) ->
                let fileName = System.IO.Path.GetFileName reader.OutputFile
                Expect.equal fileName "csharp-lib.dll" "Should have found the C# lib"
            | _ -> failwith "Should have found a C# reference"
        )

let referenceAssemblySupportTest toolsPath prefix (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
    |> withLog
        $"{prefix} can reference projects that support reference assemblies"
        (fun logger fs ->
            let parentProj: TestAssetProjInfo = ``NetSDK library with ProduceReferenceAssembly``
            let childProj = ``NetSDK library referencing ProduceReferenceAssembly library``

            let projPath = pathForProject childProj

            // need to build the projects first so that there's something to latch on to
            dotnet fs [
                "build"
                projPath
            ]
            |> checkExitCodeZero

            let loader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            Expect.hasLength parsed 2 "Should have loaded the F# lib and the referenced F# lib"

            let fsharpProject =
                parsed
                |> Seq.find (fun p -> Path.GetFileName(p.ProjectFileName) = Path.GetFileName(childProj.ProjectFile))

            let mapped = FCS.mapToFSharpProjectOptions fsharpProject parsed
            let referencedProjects = mapped.ReferencedProjects
            Expect.hasLength referencedProjects 1 "Should have a reference to the F# ProjectReference lib"

            match referencedProjects[0] with
            | FSharpReferencedProject.FSharpReference(targetPath, _) -> Expect.stringContains targetPath (refAssemblyForProject parentProj) "Should have found the ref assembly for the F# lib"
            | _ -> failwith "Should have found a F# reference"
        )

let testProjectLoadBadData =
    testCase
    |> withLog
        "Does not crash when loading malformed cache data"
        (fun logger fs ->
            let testDir = inDir fs "sample_netsdk_bad_cache"
            copyDirFromAssets fs ``sample NetSdk library with a bad FSAC cache``.ProjDir testDir
            let projFile = Path.Combine(testDir, "n1", "n1.fsproj")
            use proj = new ProjectSystem.Project(projFile, ignore)
            Expect.isNone proj.Response "should have loaded, detected bad data, and defaulted to empty"
        )

let canLoadMissingImports toolsPath loaderType (workspaceFactory: ToolsPath -> IWorkspaceLoader) =
    testCase
        $"Can load projects with missing Imports - {loaderType}"
        (fun () ->
            let proj = ``Console app with missing direct Import``
            let projPath = pathForProject proj

            let loader = workspaceFactory toolsPath
            let logger = Log.create (sprintf "Test '%s'" $"Can load projects with missing Imports - {loaderType}")

            loader.Notifications.Add(
                function
                | WorkspaceProjectState.Failed(projPath, errors) ->
                    logger.error (
                        Message.eventX "Failed to load project {project} with {errors}"
                        >> Message.setField "project" projPath
                        >> Message.setField "errors" errors
                    )
                | WorkspaceProjectState.Loading p ->
                    logger.info (
                        Message.eventX "Loading project {project}"
                        >> Message.setField "project" p
                    )
                | WorkspaceProjectState.Loaded(p, knownProjects, fromCache) ->
                    logger.info (
                        Message.eventX "Loaded project {project}(fromCache: {fromCache})"
                        >> Message.setField "project" p
                        >> Message.setField "fromCache" fromCache
                    )
            )

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            Expect.equal parsed.Length 1 "Should have loaded the project"
            let parsed = parsed[0]
            Expect.equal 3 parsed.SourceFiles.Length "Should have Program.fs, AssemblyInfo, and AssemblyAttributes"
            Expect.stringEnds parsed.SourceFiles[2] "Program.fs" "Filename should be Program.fs"
        )

let traversalProjectTest toolsPath loaderType workspaceFactory =
    testCase
        $"can crack traversal projects - {loaderType}"
        (fun () ->
            let logger = Log.create "Test 'can crack traversal projects'"
            let fs = FileUtils(logger)
            let projPath = pathForProject ``traversal project``
            // // need to build the projects first so that there's something to latch on to
            // dotnet fs [
            //     "build"
            //     projPath
            //     "-bl"
            // ]
            // |> checkExitCodeZero

            let loader: IWorkspaceLoader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects [ projPath ]
                |> Seq.toList

            Expect.hasLength parsed 3 "Should have loaded the 3 referenced projects from the traversal project"

        )

let sample11OtherProjectsTest toolsPath loaderType workspaceFactory =
    testCase
        $"Can load sample11 with other projects like shproj in sln - {loaderType}"
        (fun () ->

            let projPath = pathForProject ``sample 11 sln with other project types``

            let projPaths =
                // using Inspectsln emulates what we do in FsAutocomplete for gathering projects to load
                InspectSln.tryParseSln projPath
                |> getResult
                |> InspectSln.loadingBuildOrder

            let loader: IWorkspaceLoader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects projPaths
                |> Seq.toList

            Expect.hasLength parsed 1 "Should have fsproj"
        )

let sample12SlnFilterTest toolsPath loaderType workspaceFactory =
    testCase
        $"Can load sample12 with solution folder with one project - {loaderType}"
        (fun () ->

            let projPath = pathForProject ``sample 12 slnf with one project``

            let projPaths =
                // using Inspectsln emulates what we do in FsAutocomplete for gathering projects to load
                InspectSln.tryParseSln projPath
                |> getResult
                |> InspectSln.loadingBuildOrder

            let loader: IWorkspaceLoader = workspaceFactory toolsPath

            let parsed =
                loader.LoadProjects projPaths
                |> Seq.toList

            Expect.hasLength parsed 1 "Should have fsproj"

            let projDir = Path.GetDirectoryName projPath

            let fsproj =
                projDir
                / ``sample 12 slnf with one project``.ProjectReferences.[0].ProjectFile

            Expect.equal parsed[0].ProjectFileName fsproj "should contain the expected project"
        )

let sample13SolutionFilesTest toolsPath loaderType workspaceFactory =
    testCase
        $"Can load sample13 sln with solution files - {loaderType}"
        (fun () ->

            let projPath = pathForProject ``sample 13 sln with solution files``
            let projDir = Path.GetDirectoryName projPath

            let expectedReadme =
                projDir
                / "README.md"

            let solutionContents =
                InspectSln.tryParseSln projPath
                |> getResult

            let solutionItem = solutionContents.Items[0]

            Expect.equal solutionItem.Guid (Guid("8ec462fd-d22e-90a8-e5ce-7e832ba40c5d")) "Should have the epxcted guid"
            Expect.equal solutionItem.Name "Solution Items" "Should have the expected folder name"

            match solutionItem.Kind with
            | InspectSln.Folder(_, files) ->
                Expect.hasLength files 1 "Should have one file"
                Expect.sequenceEqual files [ expectedReadme ] "Should contain the expected readme.md"
            | _ -> failtestf "Expected a folder, but got %A" solutionItem.Kind
        )

let tests toolsPath =
    let testSample3WorkspaceLoaderExpected = [
        ExpectNotification.loading "c1.fsproj"
        ExpectNotification.loading "l1.csproj"
        ExpectNotification.loaded "l1.csproj"
        ExpectNotification.loading "l2.fsproj"
        ExpectNotification.loaded "l2.fsproj"
        ExpectNotification.loaded "c1.fsproj"
    ]

    let testSample3GraphExpected = [
        ExpectNotification.loading "c1.fsproj"
        ExpectNotification.loaded "c1.fsproj"
    ]

    let testSlnExpected = [
        ExpectNotification.loading "c1.fsproj"
        ExpectNotification.loading "l2.fsproj"
        ExpectNotification.loaded "l2.fsproj"
        ExpectNotification.loaded "c1.fsproj"
        ExpectNotification.loading "l1.fsproj"
        ExpectNotification.loaded "l1.fsproj"
        ExpectNotification.loaded "l2.fsproj"
    ]

    let testSlnGraphExpected = [
        ExpectNotification.loading "l2.fsproj"
        ExpectNotification.loading "l1.fsproj"
        ExpectNotification.loading "c1.fsproj"
        ExpectNotification.loaded "l2.fsproj"
        ExpectNotification.loaded "c1.fsproj"
        ExpectNotification.loaded "l1.fsproj"
    ]


    testSequenced
    <| testList "Main tests" [
        testSample2 toolsPath "WorkspaceLoader" false (fun (tools, props) -> WorkspaceLoader.Create(tools, globalProperties = props))
        testSample2 toolsPath "WorkspaceLoader" true (fun (tools, props) -> WorkspaceLoader.Create(tools, globalProperties = props))
        testSample2 toolsPath "WorkspaceLoaderViaProjectGraph" false (fun (tools, props) -> WorkspaceLoaderViaProjectGraph.Create(tools, globalProperties = props))
        testSample2 toolsPath "WorkspaceLoaderViaProjectGraph" true (fun (tools, props) -> WorkspaceLoaderViaProjectGraph.Create(tools, globalProperties = props))
        //   testSample3 toolsPath "WorkspaceLoader" WorkspaceLoader.Create testSample3WorkspaceLoaderExpected //- Sample 3 having issues, was also marked pending on old test suite
        //   testSample3 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create testSample3GraphExpected //- Sample 3 having issues, was also marked pending on old test suite
        testSample4 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testSample4 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testSample5 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testSample5 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testSample9 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testSample9 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testSample10 toolsPath "WorkspaceLoader" false (fun (tools, props) -> WorkspaceLoader.Create(tools, globalProperties = props))
        testSample10 toolsPath "WorkspaceLoaderViaProjectGraph" false (fun (tools, props) -> WorkspaceLoaderViaProjectGraph.Create(tools, globalProperties = props))
        //Sln tests
        //   testLoadSln toolsPath "WorkspaceLoader" WorkspaceLoader.Create testSlnExpected // Having issues on CI
        //   testLoadSln toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create testSlnGraphExpected // Having issues on CI
        //   testParseSln toolsPath
        //Render tests
        testRender2 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testRender2 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        //   testRender3 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        //   testRender3 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create //- Sample 3 having issues, was also marked pending on old test suite
        testRender4 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testRender4 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testRender5 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testRender5 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testRender8 toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testRender8 toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        //Invalid tests
        testProjectNotFound toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testProjectNotFound toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        //FCS tests
        testFCSmap toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testFCSmap toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        //FCS multi-project tests
        testFCSmapManyProj toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testFCSmapManyProj toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testFCSmapManyProjCheckCaching
        //ProjectSystem tests
        testProjectSystem toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testProjectSystem toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testProjectSystemOnChange toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testProjectSystemOnChange toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        debugTests toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        debugTests toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        testProjectSystemCacheLoad toolsPath "WorkspaceLoader" WorkspaceLoader.Create

        //loadProject test
        testLoadProject toolsPath
        loadProjfileFromDiskTests toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        loadProjfileFromDiskTests toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create

        //Binlog test
        testSample2WithBinLog "n1.binlog" toolsPath "WorkspaceLoader" WorkspaceLoader.Create
        testSample2WithBinLog "graph-build.binlog" toolsPath "WorkspaceLoaderViaProjectGraph" WorkspaceLoaderViaProjectGraph.Create
        test "can get runtimes" {
            let runtimes =
                SdkDiscovery.runtimes (
                    Paths.dotnetRoot.Value
                    |> Option.defaultWith (fun _ -> failwith "unable to find dotnet binary")
                )

            Expect.isNonEmpty runtimes "should have found at least the currently-executing runtime"
        }
        test "can get sdks" {
            let sdks =
                SdkDiscovery.sdks (
                    Paths.dotnetRoot.Value
                    |> Option.defaultWith (fun _ -> failwith "unable to find dotnet binary")
                )

            Expect.isNonEmpty sdks "should have found at least the currently-executing sdk"
        }
        testLegacyFrameworkProject toolsPath "can load legacy project file" false (fun (tools, props) -> WorkspaceLoader.Create(tools, globalProperties = props))
        testLegacyFrameworkMultiProject toolsPath "can load legacy multi project file" false (fun (tools, props) -> WorkspaceLoader.Create(tools, globalProperties = props))
        testProjectLoadBadData
        expensiveTests toolsPath WorkspaceLoader.Create
        csharpLibTest toolsPath WorkspaceLoader.Create

        referenceAssemblySupportTest toolsPath (nameof (WorkspaceLoader)) WorkspaceLoader.Create
        referenceAssemblySupportTest toolsPath (nameof (WorkspaceLoaderViaProjectGraph)) WorkspaceLoaderViaProjectGraph.Create

        // tests that cover our ability to handle missing imports
        canLoadMissingImports toolsPath (nameof (WorkspaceLoader)) WorkspaceLoader.Create
        canLoadMissingImports toolsPath (nameof (WorkspaceLoaderViaProjectGraph)) WorkspaceLoaderViaProjectGraph.Create

        traversalProjectTest toolsPath (nameof (WorkspaceLoader)) WorkspaceLoader.Create
        traversalProjectTest toolsPath (nameof (WorkspaceLoaderViaProjectGraph)) WorkspaceLoaderViaProjectGraph.Create

        sample11OtherProjectsTest toolsPath (nameof (WorkspaceLoader)) WorkspaceLoader.Create
        sample11OtherProjectsTest toolsPath (nameof (WorkspaceLoaderViaProjectGraph)) WorkspaceLoaderViaProjectGraph.Create

        sample12SlnFilterTest toolsPath (nameof (WorkspaceLoader)) WorkspaceLoader.Create
        sample12SlnFilterTest toolsPath (nameof (WorkspaceLoaderViaProjectGraph)) WorkspaceLoaderViaProjectGraph.Create

        sample13SolutionFilesTest toolsPath (nameof (WorkspaceLoader)) WorkspaceLoader.Create
        sample13SolutionFilesTest toolsPath (nameof (WorkspaceLoaderViaProjectGraph)) WorkspaceLoaderViaProjectGraph.Create
    ]
