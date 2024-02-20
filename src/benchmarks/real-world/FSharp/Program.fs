module FSharp.Benchmarks.Program

open System.IO
open System.Xml
open System.Collections.Immutable

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open FSharp.Compiler.Tokenization

open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open BenchmarkDotNet.Extensions

[<Literal>]
let FSharpCategory = "fsharp"

let (++) a b = Path.Combine(a, b)

let prepareProject projectDirName =
    let projectDir = __SOURCE_DIRECTORY__ ++ projectDirName
    
    let projectFile =
        projectDir
        |> Directory.GetFiles
        |> Seq.filter (fun f -> f.EndsWith ".fsproj")
        |> Seq.toList
        |> function
            | [] -> failwith $"No .fsproj file found in {projectDir}"
            | [x] -> x
            | files -> failwith $"Multiple .fsproj files found in {projectDir}: {files}"
    
    let fsproj = XmlDocument()
    do fsproj.Load projectFile
    
    let sourceFiles = [|for node in fsproj.DocumentElement.SelectNodes("//Compile") -> projectDir ++ node.Attributes["Include"].InnerText|] 
    
    let checker = FSharpChecker.Create(projectCacheSize=300)

    let projectOptions, _diagnostics =
        checker.GetProjectOptionsFromScript("file.fs", SourceText.ofString "", assumeDotNetFramework=false)
        |> Async.RunSynchronously
    
    let projectOptions =
        { projectOptions with
            OtherOptions = [|
                yield! projectOptions.OtherOptions
                "--optimize+"
                "--target:library"
            |]
            UseScriptResolutionRules = false
            ProjectFileName = projectFile
            SourceFiles = sourceFiles }
        
    projectDir, projectOptions, checker
    
let parseAndTypeCheckProject (projectDir, projectOptions, checker: FSharpChecker)  =    

    let result = checker.ParseAndCheckProject(projectOptions) |> Async.RunSynchronously
    
    match result.Diagnostics |> Seq.where (fun d -> d.Severity = FSharpDiagnosticSeverity.Error) |> Seq.toList with
    | [] -> projectDir, projectOptions, checker
    | errors ->
        let errors = errors |> Seq.map (sprintf "%A") |> String.concat "\n" 
        failwith $"Type checking failed {errors}"

let counter = (Seq.initInfinite id).GetEnumerator()

let typeCheckFileInProject projectDir projectOptions (checker: FSharpChecker) filename =
    let filename = projectDir ++ filename
    
    counter.MoveNext() |> ignore
    let count = counter.Current
    let contents = File.ReadAllText filename + $"\n// {count}" // avoid cache
    
    let _parseResult, checkResult = checker.ParseAndCheckFileInProject(filename, count, SourceText.ofString contents, projectOptions) |> Async.RunSynchronously
           
    match checkResult with
    | FSharpCheckFileAnswer.Succeeded checkFileResults ->
        match checkFileResults.Diagnostics |> Seq.where (fun d -> d.Severity = FSharpDiagnosticSeverity.Error) |> Seq.toList with
        | [] -> checkFileResults
        | errors ->
            let errors = errors |> Seq.map (sprintf "%A") |> String.concat "\n" 
            failwith $"Type checking failed {errors}"
    | FSharpCheckFileAnswer.Aborted -> failwith "Type checking aborted"   


[<BenchmarkCategory(FSharpCategory)>]
type FsPlusBenchmarks () =
    
    let projectDir, projectOptions, checker = prepareProject "FSharpPlus" |> parseAndTypeCheckProject
   
    [<Benchmark>]
    member this.TypeCheckControlMonoid () =
        let filename = "Control" ++ "Monoid.fs"
        typeCheckFileInProject projectDir projectOptions checker filename
        
    [<Benchmark>]
    member this.TypeCheckDataFree () =
        let filename = "Data" ++ "Free.fs"
        typeCheckFileInProject projectDir projectOptions checker filename


[<EntryPoint>]
let main args =
    let assembly = typeof<FsPlusBenchmarks>.Assembly
    BenchmarkSwitcher
        .FromAssembly(assembly)
        .Run(args, RecommendedConfig.Create(
            artifactsPath = DirectoryInfo(Path.GetDirectoryName(assembly.Location) ++ "BenchmarkDotNet.Artifacts"),
            mandatoryCategories = ImmutableHashSet.Create FSharpCategory))
        .ToExitCode()
