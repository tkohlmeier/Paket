﻿module internal Paket.PackageMetaData

open Paket
open System
open System.IO
open System.Reflection
open Paket.Domain
open Paket.Logging
open System.Collections.Generic

let (|CompleteTemplate|IncompleteTemplate|) templateFile = 
    match templateFile with
    | { Contents = (CompleteInfo(core, optional)) } -> CompleteTemplate(core, optional)
    | _ -> IncompleteTemplate

let (|Title|Description|Version|InformationalVersion|Company|Ignore|) (attribute : obj) = 
    match attribute with
    | :? AssemblyTitleAttribute as title ->
        match title.Title with
        | x when String.IsNullOrWhiteSpace x ->
            Ignore
        | x ->
            Title x
    | :? AssemblyDescriptionAttribute as description ->
        match description.Description with
        | x when String.IsNullOrWhiteSpace x ->
            Ignore
        | x ->
            Description x
    | :? AssemblyVersionAttribute as version ->
        match version.Version with
        | x when String.IsNullOrWhiteSpace x ->
            Ignore
        | x -> Version(SemVer.Parse x)
    | :? AssemblyInformationalVersionAttribute as version -> 
        match version.InformationalVersion with
        | x when String.IsNullOrWhiteSpace x ->
            Ignore
        | x ->
            InformationalVersion(SemVer.Parse x)
    | :? AssemblyCompanyAttribute as company ->
        match company.Company with
        | x when String.IsNullOrWhiteSpace x ->
            Ignore
        | x -> Company x
    | _ -> Ignore

let getId (assembly : Assembly) (md : ProjectCoreInfo) = { md with Id = Some(assembly.GetName().Name) }

let getVersion (assembly : Assembly) attributes (md : ProjectCoreInfo) = 
    let version = 
        let informational = 
            attributes |> Seq.tryPick (function 
                              | InformationalVersion v -> Some v
                              | _ -> None)
        match informational with
        | Some v -> informational
        | None -> 
            let fromAssembly = 
                match assembly.GetName().Version with
                | null -> None
                | v -> Some(SemVer.Parse(v.ToString()))
            match fromAssembly with
            | Some v -> fromAssembly
            | None -> 
                attributes |> Seq.tryPick (function 
                                  | Version v -> Some v
                                  | _ -> None)
    { md with Version = version }

let getAuthors attributes (md : ProjectCoreInfo) = 
    let authors = 
        attributes
        |> Seq.tryPick (function 
               | Company a -> Some a
               | _ -> None)
        |> Option.map (fun a -> 
               a.Split(',')
               |> Array.map (fun s -> s.Trim())
               |> List.ofArray)
    { md with Authors = authors }

let getDescription attributes (md : ProjectCoreInfo) = 
    { md with Description = 
                  attributes |> Seq.tryPick (function 
                                    | Description d -> Some d
                                    | _ -> None) }

let loadAssemblyMetadata buildConfig (projectFile : ProjectFile) = 
    let fileName = 
        Path.Combine
            (Path.GetDirectoryName projectFile.FileName, projectFile.GetOutputDirectory buildConfig, 
             projectFile.GetAssemblyName()) |> normalizePath

    traceVerbose <| sprintf "Loading assembly metadata for %s" fileName
    let bytes = File.ReadAllBytes fileName
    let assembly = Assembly.Load bytes
    let attribs = 
        try
            assembly.GetCustomAttributes(true)
        with
        | exn -> 
            traceWarnfn "Loading custom attributes failed for %s.%sMessage: %s" assembly.FullName Environment.NewLine exn.Message
            assembly.GetCustomAttributes(false)

    ProjectCoreInfo.Empty
    |> getId assembly
    |> getVersion assembly attribs
    |> getAuthors attribs
    |> getDescription attribs

let (|Valid|Invalid|) md = 
    match md with
    | { ProjectCoreInfo.Id = Some id'; Version = Some v; Authors = Some a; Description = Some d } -> 
        Valid { CompleteCoreInfo.Id = id'
                Version = Some v
                Authors = a
                Description = d }
    | _ -> Invalid

let addDependency (templateFile : TemplateFile) (dependency : string * VersionRequirement) = 
    match templateFile with
    | CompleteTemplate(core, opt) -> 
        { FileName = templateFile.FileName
          Contents = CompleteInfo(core, { opt with Dependencies = dependency :: opt.Dependencies }) }
    | IncompleteTemplate -> 
        failwith "You should only try and add dependencies to template files with complete metadata."

let toFile config (p : ProjectFile) = 
    Path.Combine(Path.GetDirectoryName p.FileName, p.GetOutputDirectory(config), p.GetAssemblyName())

let addFile (source : string) (dest : string) (templateFile : TemplateFile) = 
    match templateFile with
    | CompleteTemplate(core, opt) -> 
        { FileName = templateFile.FileName
          Contents = CompleteInfo(core, { opt with Files = (source,dest) :: opt.Files }) }
    | IncompleteTemplate -> 
        failwith "You should only try and add dependencies to template files with complete metadata."

let findDependencies (dependencies : DependenciesFile) config (template : TemplateFile) (project : ProjectFile) 
    (map : Map<string, TemplateFile * ProjectFile>) = 
    let targetDir = 
        match project.OutputType with
        | ProjectOutputType.Exe -> "tools/"
        | ProjectOutputType.Library -> sprintf "lib/%s/" (project.GetTargetFramework().ToString())
    
    let projectDir = Path.GetDirectoryName project.FileName
    
    let deps, files = 
        project.GetInterProjectDependencies() 
        |> Seq.fold (fun (deps, files) p -> 
            match Map.tryFind p.Name map with
            | Some packagedRef -> packagedRef :: deps, files
            | None -> 
                let p = 
                    match ProjectFile.Load(Path.Combine(projectDir, p.Path)) with
                    | Some p -> p
                    | _ -> failwithf "Missing project reference in proj file %s" p.Path
                    
                deps, p :: files) ([], [])
    
    // Add the assembly from this project
    let withOutput = addFile (toFile config project) targetDir template
    
    // If project refs will also be packaged, add dependency
    let withDeps = 
        deps
        |> List.map (fun (templateFile,_) ->
            match templateFile with
            | CompleteTemplate(core, opt) -> 
                match core.Version with
                | Some v -> core.Id, VersionRequirement(Minimum(v), PreReleaseStatus.All)
                | none ->failwithf "There was no versin given for %s." templateFile.FileName
            | IncompleteTemplate -> failwithf "You cannot create a dependency on a template file (%s) with incomplete metadata." templateFile.FileName)
        |> List.fold addDependency withOutput
    
    // If project refs will not be packaged, add the assembly to the package
    let withDepsAndIncluded = 
        files
        |> List.fold (fun templatefile file -> addFile (toFile config file) targetDir templatefile) withDeps
    
    // Add any paket references
    let referenceFile = 
        ProjectFile.FindReferencesFile <| FileInfo project.FileName |> Option.map (ReferencesFile.FromFile)
    match referenceFile with
    | Some r -> 
        r.NugetPackages
        |> List.map (fun np -> np.Name.Id, dependencies.DirectDependencies.[np.Name])
        |> List.fold addDependency withDepsAndIncluded
    | None -> withDepsAndIncluded
