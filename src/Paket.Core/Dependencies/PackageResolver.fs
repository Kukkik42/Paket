/// Contains logic which helps to resolve the dependency graph.
module Paket.PackageResolver

open Paket
open Paket.Domain
open Paket.Requirements
open Paket.Logging
open System.Collections.Generic
open System
open System.Diagnostics
open Paket.PackageSources
open System.Threading.Tasks
open System.Threading
open FSharp.Polyfill
open System.Text.RegularExpressions


type DependencySet = Set<PackageName * VersionRequirement * FrameworkRestrictions>

/// Represents package details
[<StructuredFormatDisplay "{Display}">]
type PackageDetails = {
    Name               : PackageName
    Source             : PackageSource
    DownloadLink       : string
    LicenseUrl         : string
    Unlisted           : bool
    DirectDependencies : DependencySet
}

type SourcePackageInfo =
  { Sources : PackageSource list 
    GroupName : GroupName
    PackageName : PackageName }
    static member ofParams sources groupName packageName =
      { Sources = sources; GroupName = groupName; PackageName = packageName }

type GetPackageDetailsParameters =
  { Package : SourcePackageInfo
    Version : SemVerInfo }
    static member ofParams sources groupName packageName version =
        SourcePackageInfo.ofParams sources groupName packageName
        |> fun p -> { Package = p; Version = version }

type GetPackageVersionsParameters =
  { Package : SourcePackageInfo }
    static member ofParams sources groupName packageName =
        SourcePackageInfo.ofParams sources groupName packageName
        |> fun p -> { Package = p }
  
type PackageDetailsFunc = GetPackageDetailsParameters -> Async<PackageDetails>
type PackageDetailsSyncFunc = GetPackageDetailsParameters -> PackageDetails
type PackageVersionsFunc = GetPackageVersionsParameters -> Async<seq<SemVerInfo * PackageSource list>>
type PackageVersionsSyncFunc = GetPackageVersionsParameters -> seq<SemVerInfo * PackageSource list>

/// Represents data about resolved packages
[<StructuredFormatDisplay "{Display}">]
type ResolvedPackage = {
    Name                : PackageName
    Version             : SemVerInfo
    Dependencies        : DependencySet
    Unlisted            : bool
    IsRuntimeDependency : bool
    IsCliTool           : bool
    Settings            : InstallSettings
    Source              : PackageSource
} with
    override this.ToString () = sprintf "%O %O" this.Name this.Version

    member self.HasFrameworkRestrictions =
        getExplicitRestriction self.Settings.FrameworkRestrictions <> FrameworkRestriction.NoRestriction

    member private self.Display
        with get() =
            let deps =
                self.Dependencies
                |> Seq.map (fun (name,ver,restrict) ->
                    sprintf "  <%A - %A - %A>\n" name ver restrict)
                |> String.Concat
            sprintf
                "%A\nDependencies -\n%s\nSource - %A\nInstall Settings\n%A"
                    self.Name deps self.Source self.Settings


type PackageResolution = Map<PackageName, ResolvedPackage>
/// Caches information retrieved by GetVersions until it is required by GetDetails
type VersionCache =
  { Version : SemVerInfo; Sources : PackageSource list; AssumedVersion : bool }
    static member ofParams version sources isAssumed =
        { Version = version; Sources = sources; AssumedVersion = isAssumed }

type ResolverStep = {
    Relax: bool
    FilteredVersions : Map<PackageName, (VersionCache list * bool)>
    CurrentResolution : Map<PackageName,ResolvedPackage>;
    ClosedRequirements : Set<PackageRequirement>
    OpenRequirements : Set<PackageRequirement> }

module DependencySetFilter =
    let isIncluded (restriction:FrameworkRestriction) (dependency:PackageName * VersionRequirement * FrameworkRestrictions) =
        let _,_,dependencyRestrictions = dependency
        let dependencyRestrictions = dependencyRestrictions |> getExplicitRestriction
        if dependencyRestrictions = FrameworkRestriction.NoRestriction then true else
        // While the dependency specifies the framework restrictions of the dependency ([ >= netstandard13 ])
        // we need to take the dependency, when the combination still contains packages.
        // NOTE: This is not forwards compatible...
        //let combined = FrameworkRestriction.And [ restriction; dependencyRestrictions ]
        //not combined.RepresentedFrameworks.IsEmpty

        // "And" is not cheap therefore we use this, 
        // because we don't want to pay the price of calculating a "simplified" formula 
        Set.intersect restriction.RepresentedFrameworks dependencyRestrictions.RepresentedFrameworks
        |> Set.isEmpty
        |> not

    let filterByRestrictions (restrictions:FrameworkRestrictions) (dependencies:DependencySet) : DependencySet =
        match getExplicitRestriction restrictions with
        | FrameworkRestriction.HasNoRestriction -> dependencies
        | restrictions ->
            dependencies
            |> Set.filter (isIncluded restrictions)

    let findFirstIncompatibility (currentStep:ResolverStep) (dependencies:DependencySet) (package:ResolvedPackage) =
        dependencies
        // exists any non-matching stuff
        |> Seq.filter (fun (name, _, _) -> name = package.Name)
        |> Seq.filter (fun (name, requirement, restriction) ->
            let allowTransitivePreleases = 
                (currentStep.ClosedRequirements |> Set.exists (fun r -> r.TransitivePrereleases && r.Name = name)) ||
                (currentStep.OpenRequirements |> Set.exists (fun r -> r.TransitivePrereleases && r.Name = name))

            not (requirement.IsInRange (package.Version, allowTransitivePreleases)))
        |> Seq.tryHead

let cleanupNames (model : PackageResolution) : PackageResolution =
    model
    |> Map.map (fun _ package ->
        { package with
            Dependencies =
                package.Dependencies
                |> Set.map (fun (name, v, d) -> model.[name].Name, v, d) })


type ConflictInfo =
  { ResolveStep    : ResolverStep
    RequirementSet : PackageRequirement Set
    Requirement    : PackageRequirement
    GetPackageVersions : (PackageName -> (SemVerInfo * PackageSource list) seq) }

[<RequireQualifiedAccess>]
[<DebuggerDisplay "{DebugDisplay()}">]
type ResolutionRaw =
| OkRaw of PackageResolution
| ConflictRaw of ConflictInfo
    member internal self.DebugDisplay() =
        match self with
        | OkRaw pkgres ->   
            pkgres |> Seq.map (fun kvp -> kvp.Key, kvp.Value)
            |> Array.ofSeq |> sprintf "Ok - %A"
        | ConflictRaw { ResolveStep = resolveStep; RequirementSet = reqSet; Requirement = req } ->
            sprintf "%A\n%A\n%A\n" resolveStep reqSet req


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ResolutionRaw =

    open System.Text

    let getConflicts (res:ResolutionRaw) =
        match res with
        | ResolutionRaw.OkRaw _ -> Set.empty
        | ResolutionRaw.ConflictRaw { ResolveStep = currentStep; Requirement = lastPackageRequirement } ->
            currentStep.ClosedRequirements
            |> Set.union currentStep.OpenRequirements
            |> Set.add lastPackageRequirement
            |> Set.filter (fun x -> x.Name = lastPackageRequirement.Name)

    let buildConflictReport (errorReport:StringBuilder) (conflicts:PackageRequirement Set) =
        let formatVR (vr:VersionRequirement) =
            vr.ToString ()
            |> fun s -> if String.IsNullOrWhiteSpace s then ">= 0" else s

        let formatPR hasPrereleases (vr:VersionRequirement) = 
            if hasPrereleases && vr.PreReleases = PreReleaseStatus.No then " (no prereleases)" else
            match vr.PreReleases with
            | PreReleaseStatus.Concrete [x] -> sprintf " (%s)" x
            | PreReleaseStatus.Concrete x -> sprintf " %A" x
            | _ -> ""

        match conflicts with
        | s when s.IsEmpty -> errorReport
        | conflicts ->
            let hasPrereleases = Seq.exists (fun r -> r.VersionRequirement.PreReleases <> PreReleaseStatus.No) conflicts

            errorReport.AddLine (sprintf "  Conflict detected:")

            let getConflictMessage req =
                let vr = formatVR req.VersionRequirement
                let pr = formatPR hasPrereleases req.VersionRequirement
                match req.Parent with
                | DependenciesFile _ ->
                    sprintf "   - Dependencies file requested package %O: %s%s" req.Name vr pr
                | Package (parentName,version,_) ->
                    sprintf "   - %O %O requested package %O: %s%s" parentName version req.Name vr pr
            
            conflicts
            |> Seq.fold (fun (errorReport:StringBuilder) conflict ->
                errorReport.AppendLine (getConflictMessage conflict)) errorReport

    let getErrorText showResolvedPackages (res:ResolutionRaw) =
        match res with
        | ResolutionRaw.OkRaw _ -> ""
        | ResolutionRaw.ConflictRaw { ResolveStep = currentStep; GetPackageVersions = getVersionF } ->
            let errorText =
                if showResolvedPackages && not currentStep.CurrentResolution.IsEmpty then
                    ( StringBuilder().AppendLine  "  Resolved packages:"
                    , currentStep.CurrentResolution)
                    ||> Map.fold (fun sb _ resolvedPackage ->
                        sb.AppendLinef "   - %O %O" resolvedPackage.Name resolvedPackage.Version)
                else StringBuilder()

            match getConflicts res with
            | c when c.IsEmpty  ->
                errorText.AppendLinef
                    "  Could not resolve package %O. Unknown resolution error."
                        (Seq.head currentStep.OpenRequirements)
            | cfs when cfs.Count = 1 ->
                let c = cfs.MinimumElement
                let errorText = buildConflictReport errorText cfs
                match getVersionF c.Name |> Seq.toList with
                | [] -> errorText.AppendLinef  "   - No versions available."
                | avalaibleVersions ->
                    ( errorText.AppendLinef  "   - Available versions:"
                    , avalaibleVersions )
                    ||> List.fold (fun sb elem -> sb.AppendLinef "     - %O" elem)
            | conflicts -> buildConflictReport errorText conflicts
            |> string

    let isDone (res:ResolutionRaw) =
        match res with
        | ResolutionRaw.OkRaw _ -> true
        | _ -> false

type ResolutionRaw with
    member self.GetConflicts () = ResolutionRaw.getConflicts self
    member self.GetErrorText showResolvedPackages = ResolutionRaw.getErrorText showResolvedPackages self
    member self.IsDone = ResolutionRaw.isDone self

[<RequireQualifiedAccess>]
[<DebuggerDisplay "{DebugDisplay()}">]
type Resolution =
    private { Raw : ResolutionRaw; Errors : Exception list }
    static member ofRaw errors resolution =
        { Raw = resolution; Errors = errors }
    member private self.DebugDisplay() = self.Raw.DebugDisplay()

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Resolution =
    
    let getConflicts (res:Resolution) = ResolutionRaw.getConflicts res.Raw
    let getErrorText showResolvedPackages (res:Resolution) = ResolutionRaw.getErrorText showResolvedPackages res.Raw
    let isDone (res:Resolution) = ResolutionRaw.isDone res.Raw
    let addError error (res:Resolution) =
        { res with Errors = error :: res.Errors }
    let addErrors errors (res:Resolution) =
        { res with Errors = errors @ res.Errors }

    let getModelOrFail (res:Resolution) = 
        match res.Raw with
        | ResolutionRaw.OkRaw model -> model
        | ResolutionRaw.ConflictRaw _ ->
            let msg =
                sprintf "There was a version conflict during package resolution.\n\
                         %s\n  Please try to relax some conditions or resolve the conflict manually (see http://fsprojects.github.io/Paket/nuget-dependencies.html#Use-exactly-this-version-constraint)." (getErrorText true res)
            raise <| AggregateException(msg, res.Errors)
    let (|Ok|Conflict|) (res:Resolution) =
        match res.Raw with
        | ResolutionRaw.OkRaw res -> Ok res
        | ResolutionRaw.ConflictRaw conf -> Conflict conf
    let Ok resolution =
        Resolution.ofRaw [] (ResolutionRaw.OkRaw resolution)

type Resolution with

    member self.GetConflicts () = Resolution.getConflicts self
    member self.GetErrors () = self.Errors
    member self.GetErrorText showResolvedPackages = Resolution.getErrorText showResolvedPackages self
    member self.GetModelOrFail () = Resolution.getModelOrFail self
    member self.IsDone = Resolution.isDone self
    member self.IsOk = self.IsDone
    member self.IsConflict = not self.IsDone


let calcOpenRequirements (exploredPackage:ResolvedPackage,globalFrameworkRestrictions,(verCache:VersionCache),dependency,resolverStep:ResolverStep) =
    let dependenciesByName =
        // there are packages which define multiple dependencies to the same package
        // we compress these here - see #567
        let dict = Dictionary<_,_>()
        exploredPackage.Dependencies
        |> Set.iter (fun ((name,v,r) as dep) ->
            match dict.TryGetValue name with
            | true,(_,v2,r2) ->
                match v,v2 with
                | VersionRequirement(ra1,p1),VersionRequirement(ra2,p2) when p1 = p2 ->
                    let newRestrictions =
                        match r with
                        | ExplicitRestriction r ->
                            match r2 with
                            | ExplicitRestriction r2 ->
                                FrameworkRestriction.combineRestrictionsWithOr r r2 |> ExplicitRestriction
                            | AutoDetectFramework -> ExplicitRestriction r
                        | AutoDetectFramework -> r

                    if ra1.IsIncludedIn ra2 then
                        dict.[name] <- (name,v,newRestrictions)
                    elif ra2.IsIncludedIn ra1 then
                        dict.[name] <- (name,v2,newRestrictions)
                    else dict.[name] <- dep
                | _ ->  dict.[name] <- dep
            | _ -> dict.Add(name,dep))
        dict
        |> Seq.map (fun kv -> kv.Value)
        |> Set.ofSeq

    let rest =
        resolverStep.OpenRequirements
        |> Set.remove dependency

    dependenciesByName
    |> Set.map (fun (n, v, restriction) ->
        let newRestrictions =
            filterRestrictions restriction exploredPackage.Settings.FrameworkRestrictions
            |> filterRestrictions globalFrameworkRestrictions
            |> fun xs -> if xs = ExplicitRestriction FrameworkRestriction.NoRestriction then exploredPackage.Settings.FrameworkRestrictions else xs

        { dependency with
            Name = n
            VersionRequirement = v
            Parent = Package(dependency.Name, verCache.Version, exploredPackage.Source)
            Graph = Set.add dependency dependency.Graph
            TransitivePrereleases = dependency.TransitivePrereleases && exploredPackage.Version.PreRelease.IsSome
            Settings = { dependency.Settings with FrameworkRestrictions = newRestrictions } })
    |> Set.filter (fun d ->
        resolverStep.ClosedRequirements
        |> Set.exists (fun x ->
            x.Name = d.Name &&
               x.Settings.FrameworkRestrictions = d.Settings.FrameworkRestrictions &&
                (x = d ||
                 x.VersionRequirement.Range.IsIncludedIn d.VersionRequirement.Range ||
                 x.VersionRequirement.Range.IsGlobalOverride))
        |> not)
    |> Set.filter (fun d ->
        resolverStep.OpenRequirements
        |> Set.exists (fun x -> x.Name = d.Name && (x = d || x.VersionRequirement.Range.IsGlobalOverride) && x.Settings.FrameworkRestrictions = d.Settings.FrameworkRestrictions)
        |> not)
    |> Set.union rest


type Resolved = {
    ResolvedPackages : Resolution
    ResolvedSourceFiles : ModuleResolver.ResolvedSourceFile list
}

let getResolverStrategy globalStrategyForDirectDependencies globalStrategyForTransitives (allRequirementsOfCurrentPackage:Set<PackageRequirement>) (currentRequirement:PackageRequirement) =
    if currentRequirement.Parent.IsRootRequirement() && Set.count allRequirementsOfCurrentPackage = 1 then
        let combined = currentRequirement.ResolverStrategyForDirectDependencies ++ globalStrategyForDirectDependencies

        defaultArg combined ResolverStrategy.Max
    else
        let combined =
            (allRequirementsOfCurrentPackage
                |> Seq.filter (fun x -> x.Depth > 0)
                |> Seq.sortBy (fun x -> x.Depth, x.ResolverStrategyForTransitives <> globalStrategyForTransitives, x.ResolverStrategyForTransitives <> Some ResolverStrategy.Max)
                |> Seq.map (fun x -> x.ResolverStrategyForTransitives)
                |> Seq.fold (++) None)
                ++ globalStrategyForTransitives

        defaultArg combined ResolverStrategy.Max

type UpdateMode =
    | UpdateGroup of GroupName
    | UpdateFiltered of GroupName * PackageFilter
    | Install
    | UpdateAll

type private PackageConfig = {
    Dependency         : PackageRequirement
    GroupName          : GroupName
    GlobalRestrictions : FrameworkRestrictions
    RootSettings       : IDictionary<PackageName,InstallSettings>
    CliTools           : Set<PackageName>
    Version            : SemVerInfo
    Sources            : PackageSource list
    UpdateMode         : UpdateMode
} with
    member self.HasGlobalRestrictions =
        not(getExplicitRestriction self.GlobalRestrictions = FrameworkRestriction.NoRestriction)

    member self.HasDependencyRestrictions =
        not(getExplicitRestriction self.Dependency.Settings.FrameworkRestrictions = FrameworkRestriction.NoRestriction)


let private updateRestrictions (pkgConfig:PackageConfig) (package:ResolvedPackage) =
    let newRestrictions =
        if  not pkgConfig.HasGlobalRestrictions
            && (FrameworkRestriction.NoRestriction = (package.Settings.FrameworkRestrictions |> getExplicitRestriction)
            ||  not pkgConfig.HasDependencyRestrictions )
        then
            FrameworkRestriction.NoRestriction
        else
            // Setting in the dependencies file
            let globalPackageSettings =
                match pkgConfig.RootSettings.TryGetValue package.Name with
                | true, s -> 
                    match s.FrameworkRestrictions with
                    | ExplicitRestriction r -> r
                    | _ -> FrameworkRestriction.NoRestriction
                | _ -> FrameworkRestriction.NoRestriction
            // Settings required for the current resolution
            let packageSettings = package.Settings.FrameworkRestrictions |> getExplicitRestriction
            // Settings required for this current dependency
            let dependencySettings = pkgConfig.Dependency.Settings.FrameworkRestrictions |> getExplicitRestriction
            // Settings defined globally
            let globalSettings = pkgConfig.GlobalRestrictions |> getExplicitRestriction
            let isRequired =
                FrameworkRestriction.Or
                  [ packageSettings
                    FrameworkRestriction.And [dependencySettings;globalSettings]]

            // We assume the user knows what he is doing
            FrameworkRestriction.And [ globalPackageSettings;isRequired ]


    { package with
        Settings = { package.Settings with FrameworkRestrictions = ExplicitRestriction newRestrictions }
    }


let private explorePackageConfig (getPackageDetailsBlock:PackageDetailsSyncFunc) (pkgConfig:PackageConfig) =
    let dependency, version = pkgConfig.Dependency, pkgConfig.Version
    let packageSources      = pkgConfig.Sources

    match pkgConfig.UpdateMode with
    | Install -> tracefn  " - %O %A" dependency.Name version
    | _ ->
        match dependency.VersionRequirement.Range with
        | Specific _ when dependency.Parent.IsRootRequirement() -> traceWarnfn " - %O is pinned to %O" dependency.Name version
        | _ ->
            let compareString = dependency.Name.CompareString
            tracefn  " - %O %A" dependency.Name version

    let newRestrictions =
        filterRestrictions dependency.Settings.FrameworkRestrictions pkgConfig.GlobalRestrictions
    try
        let packageDetails : PackageDetails =
            getPackageDetailsBlock (GetPackageDetailsParameters.ofParams packageSources pkgConfig.GroupName dependency.Name version)
        let filteredDependencies =
            DependencySetFilter.filterByRestrictions newRestrictions packageDetails.DirectDependencies
        let settings =
            match dependency.Parent with
            | DependenciesFile _ -> dependency.Settings
            | Package _ ->
                match pkgConfig.RootSettings.TryGetValue packageDetails.Name with
                | true, s -> s + dependency.Settings
                | _ -> dependency.Settings
            |> fun x -> x.AdjustWithSpecialCases packageDetails.Name
        Result.Ok
            { Name                = packageDetails.Name
              Version             = version
              Dependencies        = filteredDependencies
              Unlisted            = packageDetails.Unlisted
              Settings            = { settings with FrameworkRestrictions = newRestrictions }
              Source              = packageDetails.Source
              IsCliTool           = Set.contains packageDetails.Name pkgConfig.CliTools
              IsRuntimeDependency = false
            }
    with
    | exn ->
        traceWarnfn "    Package not available.%s      Message: %s" Environment.NewLine exn.Message
        Result.Error (System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture exn)


type StackPack = {
    ExploredPackages     : Dictionary<PackageName*SemVerInfo,ResolvedPackage>
    KnownConflicts       : HashSet<HashSet<PackageRequirement> * (VersionCache list * bool) option>
    ConflictHistory      : Dictionary<PackageName, int>
}


let private getExploredPackage (pkgConfig:PackageConfig) (getPackageDetailsBlock:PackageDetailsSyncFunc) (stackpack:StackPack) =
    let key = (pkgConfig.Dependency.Name, pkgConfig.Version)

    match stackpack.ExploredPackages.TryGetValue key with
    | true, package ->
        let package = updateRestrictions pkgConfig package
        stackpack.ExploredPackages.[key] <- package
        if verbose then
            verbosefn "   Retrieved Explored Package  %O" package
        stackpack, Result.Ok(true, package)
    | false,_ ->
        match explorePackageConfig getPackageDetailsBlock pkgConfig with
        | Result.Ok explored ->
            if verbose then
                verbosefn "   Found Explored Package  %O" explored
            stackpack.ExploredPackages.Add(key,explored)
            stackpack, Result.Ok(false, explored)
        | Result.Error err ->
            stackpack, Result.Error err



let private getCompatibleVersions
               (currentStep:ResolverStep)
                groupName
               (currentRequirement:PackageRequirement)
               (getVersionsF: ResolverStrategy -> PackageVersionsSyncFunc)
                globalOverride
                globalStrategyForDirectDependencies
                globalStrategyForTransitives        =
    if verbose then
        verbosefn "  Trying to resolve %O" currentRequirement

    match Map.tryFind currentRequirement.Name currentStep.FilteredVersions with
    | None ->
        let allRequirementsOfCurrentPackage =
            currentStep.OpenRequirements
            |> Set.filter (fun r -> currentRequirement.Name = r.Name)

        // we didn't select a version yet so all versions are possible
        let isInRange mapF (cache:VersionCache) =
            allRequirementsOfCurrentPackage
            |> Set.forall (fun r -> (mapF r).VersionRequirement.IsInRange cache.Version)

        let getSingleVersion v =
            let sources = 
                match currentRequirement.Parent with
                | PackageRequirementSource.Package(_,_,parentSource) ->
                    parentSource :: currentRequirement.Sources |> List.distinct
                | _ ->
                    currentRequirement.Sources |> List.sortBy (fun x -> not x.IsLocalFeed, String.containsIgnoreCase "nuget.org" x.Url |> not)
            Seq.singleton (VersionCache.ofParams v sources true)

        let availableVersions =
            let resolverStrategy = getResolverStrategy globalStrategyForDirectDependencies globalStrategyForTransitives allRequirementsOfCurrentPackage currentRequirement
            let allVersions =
                getVersionsF resolverStrategy (GetPackageVersionsParameters.ofParams currentRequirement.Sources groupName currentRequirement.Name)
                |> Seq.map (fun (v, sources) -> VersionCache.ofParams v sources false)
            match currentRequirement.VersionRequirement.Range with
            | Specific v
            | OverrideAll v ->
                let results =
                    allVersions
                    |> Seq.filter (fun cache -> cache.Version = v)
                    |> Seq.toList
                match results with
                | [] -> getSingleVersion v 
                | _ -> List.toSeq results
            | _ -> allVersions
                
        let compatibleVersions = Seq.filter (isInRange id) availableVersions |> Seq.cache

        let compatibleVersions, globalOverride =
            if currentRequirement.VersionRequirement.Range.IsGlobalOverride then
                compatibleVersions, true
            elif Seq.isEmpty compatibleVersions && currentRequirement.TransitivePrereleases && not (currentRequirement.Parent.IsRootRequirement()) then
                Seq.filter (isInRange (fun r -> r.IncludingPrereleases(PreReleaseStatus.All))) availableVersions |> Seq.cache, globalOverride
            elif Seq.isEmpty compatibleVersions then

                let prereleaseStatus (r:PackageRequirement) =
                    if r.Parent.IsRootRequirement() && r.VersionRequirement <> VersionRequirement.AllReleases then
                        r.VersionRequirement.PreReleases
                    else
                        PreReleaseStatus.All

                let available = availableVersions |> Seq.toList
                let allPrereleases = available |> List.filter (fun (cache:VersionCache) -> cache.Version.PreRelease <> None) = available
                let prereleases = List.filter (isInRange (fun r -> r.IncludingPrereleases(prereleaseStatus r))) available
                if allPrereleases then
                    Seq.ofList prereleases, globalOverride
                else
                    compatibleVersions, globalOverride
            else
                compatibleVersions, globalOverride

        compatibleVersions, globalOverride, false

    | Some(versions,globalOverride) ->
        // we already selected a version so we can't pick a different
        let compatibleVersions, tryRelaxed =
            if globalOverride then List.toSeq versions, false else
            let compat =
                versions
                |> Seq.filter (fun (cache) -> currentRequirement.VersionRequirement.IsInRange(cache.Version,currentRequirement.Parent.IsRootRequirement() |> not)) 

            if Seq.isEmpty compat then
                let withPrereleases = 
                    versions
                    |> Seq.filter (fun (cache) -> currentRequirement.IncludingPrereleases().VersionRequirement.IsInRange(cache.Version,currentRequirement.Parent.IsRootRequirement() |> not))
                if currentStep.Relax || Seq.isEmpty withPrereleases then
                    withPrereleases, false
                else
                    withPrereleases, true
            else
                compat, false
        compatibleVersions, false, tryRelaxed


let private getConflicts (currentStep:ResolverStep) (currentRequirement:PackageRequirement) (knownConflicts:HashSet<HashSet<PackageRequirement> * (VersionCache list * bool) option>) =
    
    let allRequirements =
        currentStep.OpenRequirements
        |> Set.filter (fun r -> r.Graph |> Set.contains currentRequirement |> not)
        |> Set.union currentStep.ClosedRequirements

    knownConflicts
    |> Seq.map (fun (conflicts,selectedVersion) ->
        let isSubset = conflicts.IsSubsetOf allRequirements
        match selectedVersion with
        | None when isSubset -> conflicts
        | Some(selectedVersion,_) ->
            let n = (Seq.head conflicts).Name
            match currentStep.FilteredVersions |> Map.tryFind n with
            | Some(v,_) when v = selectedVersion && isSubset -> conflicts
            | _ -> HashSet()
        | _ -> HashSet())
    |> Seq.collect id
    |> HashSet


let private getCurrentRequirement packageFilter (openRequirements:Set<PackageRequirement>) (conflictHistory:Dictionary<_,_>) =
    let initialMin = Seq.head openRequirements
    let initialBoost = 0

    let currentMin, _ =
        ((initialMin,initialBoost),openRequirements)
        ||> Seq.fold (fun (cmin,cboost) d ->
            let boost =
                match conflictHistory.TryGetValue d.Name with
                | true,c -> -c
                | _ -> 0
            if PackageRequirement.Compare(d,cmin,packageFilter,boost,cboost) = -1 then
                d, boost
            else
                cmin,cboost)
    currentMin


[<StructuredFormatDisplay "{Display}">]
type ConflictState = {
    Errors               : Exception list
    Status               : ResolutionRaw
    LastConflictReported : DateTime
    TryRelaxed           : bool
    Conflicts            : Set<PackageRequirement>
    VersionsToExplore    : seq<VersionCache>
    GlobalOverride       : bool
} with
    member x.AddError exn =
        { x with Errors = exn :: x.Errors }

    member private self.Display 
        with get () = 
            let conflicts = self.Conflicts |> Seq.map (printfn "%A\n") |> String.Concat
            let explore = self.VersionsToExplore |> Seq.map (printfn "%A\n") |> String.Concat
            sprintf 
               "[< ConflictState >]\n\
                | Status       - %A\n\
                | Conflicts    - %A\n\
                | ExploreVers  - %A\n\
                | TryRelaxed   - %A\n
                | LastReport   - %A\n"                
                    self.Status conflicts explore 
                    self.TryRelaxed self.LastConflictReported
        
        
let private boostConflicts
                    (filteredVersions:Map<PackageName, (VersionCache list * bool)>)
                    (currentRequirement:PackageRequirement)
                    (stackpack:StackPack)
                    (conflictState:ConflictState) =
    let conflictStatus = conflictState.Status
    let isNewConflict =
        match stackpack.ConflictHistory.TryGetValue currentRequirement.Name with
        | true,count ->
            stackpack.ConflictHistory.[currentRequirement.Name] <- count + 1
            false
        | _ ->
            stackpack.ConflictHistory.Add(currentRequirement.Name, 1)
            true

    let conflicts = conflictStatus.GetConflicts()
    let lastConflictReported =
        match conflicts with
        | _ when not conflicts.IsEmpty  ->
            let c = conflicts |> Seq.minBy (fun c -> c.Parent)
            let selectedVersion = Map.tryFind c.Name filteredVersions
            let key = conflicts |> HashSet,selectedVersion
            stackpack.KnownConflicts.Add key |> ignore

            let reportThatResolverIsTakingLongerThanExpected =
                not isNewConflict && DateTime.Now - conflictState.LastConflictReported > TimeSpan.FromSeconds 10.

            if Logging.verbose then
                tracefn "%s" <| conflictStatus.GetErrorText(false)
                tracefn "    ==> Trying different resolution."
            if reportThatResolverIsTakingLongerThanExpected then
                traceWarnfn "%s" <| conflictStatus.GetErrorText(false)
                traceWarn "The process is taking longer than expected."
                traceWarn "Paket may still find a valid resolution, but this might take a while."
                DateTime.Now
            else
                conflictState.LastConflictReported
        | _ -> conflictState.LastConflictReported
    { conflictState with
        LastConflictReported = lastConflictReported }, stackpack


[<Struct>]
type private StepFlags = {
    Ready          : bool
    UseUnlisted    : bool
    HasUnlisted    : bool
    ForceBreak     : bool
    FirstTrial     : bool
    UnlistedSearch : bool
} with
    override self.ToString () =
        sprintf 
            "[< FLAGS >]\n\
            | Ready          - %b\n\
            | UseUnlisted    - %b\n\
            | HasUnlisted    - %b\n\
            | ForceBreak     - %b\n\
            | FirstTrial     - %b\n\   
            | UnlistedSearch - %b\n"   
            self.Ready self.UseUnlisted self.HasUnlisted self.ForceBreak self.FirstTrial self.UnlistedSearch

type private Stage =
    | Step  of currentConflict : (ConflictState * ResolverStep * PackageRequirement) * priorConflictSteps : (ConflictState * ResolverStep * PackageRequirement *  seq<VersionCache> * StepFlags) list
    | Outer of currentConflict : (ConflictState * ResolverStep * PackageRequirement) * priorConflictSteps : (ConflictState * ResolverStep * PackageRequirement *  seq<VersionCache> * StepFlags) list
    | Inner of currentConflict : (ConflictState * ResolverStep * PackageRequirement) * priorConflictSteps : (ConflictState * ResolverStep * PackageRequirement *  seq<VersionCache> * StepFlags) list

type WorkPriority =
    | BackgroundWork = 10
    | MightBeRequired = 5
    | LikelyRequired = 3
    | BlockingWork = 1

type RequestWork =
    private
        { StartWork : CancellationToken -> Task
          mutable Priority : WorkPriority }

type WorkHandle<'a> = private { Work : RequestWork; TaskSource : TaskCompletionSource<'a> }
and ResolverRequestQueue =
    private { DynamicQueue : ResizeArray<RequestWork>; Lock : obj; WaitingWorker : ResizeArray<TaskCompletionSource<RequestWork option>> }
    // callback in a lock is bad practice -> private
    member private x.With callback =
        lock x.Lock (fun () ->
            callback x.DynamicQueue x.WaitingWorker
        )
    member x.AddWork w =
        x.With (fun queue workers ->
            if workers.Count > 0 then
                let worker = workers.[0]
                workers.RemoveAt(0)
                worker.SetResult (Some w)
            else
                queue.Add(w)
        )
    member x.GetWork (ct:CancellationToken) =
        let tcs = new TaskCompletionSource<_>()
        let registration = ct.Register(fun () -> tcs.TrySetResult None |> ignore)
        tcs.Task.ContinueWith (fun (t:Task) ->
            registration.Dispose()) |> ignore
        x.With (fun queue workers ->
            if queue.Count = 0 then
                workers.Add(tcs)
            else
                let (index, work) = queue |> Seq.mapi (fun i w -> i,w) |> Seq.minBy (fun (_,w) -> w.Priority)
                queue.RemoveAt index
                tcs.TrySetResult (Some work) |> ignore
            tcs.Task
        )

module ResolverRequestQueue =
    open System.Threading

    let Create() = { DynamicQueue = new ResizeArray<RequestWork>(); Lock = new obj(); WaitingWorker = new ResizeArray<_>() }
    let addWork prio (f: CancellationToken -> Task<'a>) (q:ResolverRequestQueue) =
        let tcs = new TaskCompletionSource<_>("WorkTask")
        let work =
            { StartWork = (fun tok ->
                // When someone is actually starting the work we need to ensure we finish it...
                let registration = tok.Register(fun () ->
                    async {
                        do! Async.Sleep 1000
                        if not tcs.Task.IsCompleted then
                            tcs.TrySetException (TimeoutException "Cancellation was requested, but wasn't honered after 1 second. We finish the task forcefully (requests might still run in the background).")
                                |> ignore
                    } |> Async.Start)
                let t =
                    try
                        f tok
                    with e ->
                        //Task.FromException (e)
                        let tcs = new TaskCompletionSource<_>()
                        tcs.SetException e
                        tcs.Task

                t.ContinueWith(fun (t:Task<'a>) ->
                    registration.Dispose()
                    if t.IsCanceled then
                        tcs.TrySetException(new TaskCanceledException(t))
                    elif t.IsFaulted then
                        tcs.TrySetException(t.Exception)
                    else tcs.TrySetResult (t.Result))
                    |> ignore
                // Important to not wait on the ContinueWith result,
                // because that one will never finish in the cancellation case
                // tcs.Task should always finish
                tcs.Task :> Task)
              Priority = prio }
        q.AddWork work
        { Work = work; TaskSource = tcs }
    let startProcessing (ct:CancellationToken) ({ DynamicQueue = queue } as q) =
        let linked = new CancellationTokenSource()
        async {
            use _reg = ct.Register(fun () ->
                linked.CancelAfter(500))
            while not ct.IsCancellationRequested do
                let! work = q.GetWork(ct) |> Async.AwaitTask
                match work with
                | Some work ->
                    do! work.StartWork(ct).ContinueWith(fun (_:Task) -> ()) |> Async.AwaitTask
                | None -> ()
        }
        |> fun a -> Async.StartAsTaskProperCancel(a, TaskCreationOptions.None, linked.Token)

type WorkHandle<'a> with
    member x.TryReprioritize onlyHigher prio =
        let { Work = work } = x
        if not onlyHigher || work.Priority > prio then
            work.Priority <- prio
    member x.Reprioritize prio =
        x.TryReprioritize false prio
    member x.Task =
        let { TaskSource = task } = x
        task.Task

type ResolverTaskMemory<'a> =
    { Work : WorkHandle<'a>; mutable WaitedAlready : bool }
    member x.Wait (timeout: int)=
        if x.WaitedAlready then
            true, x.Work.Task.IsCompleted
        else
            x.WaitedAlready <- true
            false, x.Work.Task.Wait timeout
module ResolverTaskMemory =
    let ofWork w = { Work = w; WaitedAlready = false }

let selectVersionsToPreload (verReq:VersionRequirement) versions =
    seq {
        match versions |> Seq.tryFind (fun v -> verReq.IsInRange(v, true)) with
        | Some verToPreload ->
            yield verToPreload, WorkPriority.LikelyRequired
        | None -> ()
        match versions |> Seq.tryFind (verReq.IsInRange) with
        | Some verToPreload ->
            yield verToPreload, WorkPriority.LikelyRequired
        | None -> ()
        for v in versions |> Seq.filter (fun v -> verReq.IsInRange(v, true)) |> Seq.tryTake 10 do
            yield v, WorkPriority.MightBeRequired
        for v in versions |> Seq.filter (verReq.IsInRange) |> Seq.tryTake 10 do
            yield v, WorkPriority.MightBeRequired
    }
    
let RequestTimeout = 180000
let WorkerCount = 6

type PreferredVersionsFunc = ResolverStrategy -> GetPackageVersionsParameters -> list<SemVerInfo * PackageSource list>
/// Resolves all direct and transitive dependencies
let Resolve (getVersionsRaw : PackageVersionsFunc, getPreferredVersionsRaw : PreferredVersionsFunc, getPackageDetailsRaw : PackageDetailsFunc, groupName:GroupName, globalStrategyForDirectDependencies, globalStrategyForTransitives, globalFrameworkRestrictions, (rootDependencies:PackageRequirement Set), updateMode : UpdateMode) =
    tracefn "Resolving packages for group %O:" groupName

    let cliToolSettings =
        rootDependencies
        |> Seq.choose (fun r ->
            match r.Parent with
            | PackageRequirementSource.DependenciesFile _ when r.IsCliTool ->
                Some r.Name
            | _ -> None)
        |> Set.ofSeq

    use d = Profile.startCategory Profile.Category.ResolverAlgorithm
    use cts = new CancellationTokenSource()
    let workerQueue = ResolverRequestQueue.Create()
    let workerCount =
        match Environment.GetEnvironmentVariable("PAKET_RESOLVER_WORKERS") with
        | a when System.String.IsNullOrWhiteSpace a -> WorkerCount
        | a ->
            match System.Int32.TryParse a with
            | true, v when v > 0 -> v
            | _ -> traceWarnfn "PAKET_RESOLVER_WORKERS is not set to a number > 0, ignoring the value and defaulting to %d" WorkerCount
                   WorkerCount
    let workers =
        // start maximal 8 requests at the same time.
        [ 1 .. workerCount ]
        |> List.map (fun _ -> ResolverRequestQueue.startProcessing cts.Token workerQueue)

    // mainly for failing unit-tests to be faster
    let taskTimeout =
        match Environment.GetEnvironmentVariable("PAKET_RESOLVER_TASK_TIMEOUT") with
        | a when System.String.IsNullOrWhiteSpace a -> RequestTimeout
        | a ->
            match System.Int32.TryParse a with
            | true, v -> v
            | _ -> traceWarnfn "PAKET_RESOLVER_TASK_TIMEOUT is not set to an interval in milliseconds, ignoring the value and defaulting to %d" RequestTimeout
                   RequestTimeout

    let getAndReport (sources:PackageSource list) blockReason (mem:ResolverTaskMemory<_>) =
        try
            let workHandle = mem.Work
            if workHandle.Task.IsCompleted then
                Profile.trackEvent (Profile.Category.ResolverAlgorithmNotBlocked blockReason)
                workHandle.Task.Result
            else
                workHandle.Reprioritize WorkPriority.BlockingWork
                use d = Profile.startCategory (Profile.Category.ResolverAlgorithmBlocked blockReason)
                let (waitedAlready, isFinished) = mem.Wait(taskTimeout)
                // When debugger is attached we just wait forever when calling .Result later ...
                // apparently the task didn't return, let's throw here
                if not isFinished (*&& not Debugger.IsAttached*) then
                    if waitedAlready then
                        raise <| TimeoutException(sprintf "Tried (again) to access an unfinished task, not waiting %d seconds this time..." (taskTimeout / 1000))
                    else
                        raise <|
                            TimeoutException(
                                (sprintf "Waited %d seconds for a request to finish.\n" (taskTimeout / 1000)) +
                                "      Check the following sources, they might be rate limiting and stopped responding:\n" +
                                "       - " + System.String.Join("\n       - ", sources |> Seq.map (fun s -> s.Url))
                            )
                if waitedAlready && isFinished then
                    // recovered
                    if verbose then traceVerbose "Recovered on a long running task..."
                let result = workHandle.Task.Result
                d.Dispose()
                result
        with :? AggregateException as a when a.InnerExceptions.Count = 1 ->
            let flat = a.Flatten()
            if flat.InnerExceptions.Count = 1 then
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(flat.InnerExceptions.[0]).Throw()
            reraise()

    let startedGetPackageDetailsRequests = System.Collections.Concurrent.ConcurrentDictionary<_,ResolverTaskMemory<_>>()
    let startRequestGetPackageDetails (details:GetPackageDetailsParameters) =
        let key = (details.Package.Sources, details.Package.PackageName, details.Version)
        startedGetPackageDetailsRequests.GetOrAdd (key, fun _ ->
            workerQueue
            |> ResolverRequestQueue.addWork WorkPriority.BackgroundWork (fun ct ->
                (getPackageDetailsRaw details : Async<PackageDetails>)
                    |> fun a -> Async.StartAsTaskProperCancel(a, cancellationToken = ct))
            |> ResolverTaskMemory.ofWork)

    let getPackageDetailsBlock (details:GetPackageDetailsParameters) =
        let workHandle = startRequestGetPackageDetails details
        try
            getAndReport details.Package.Sources Profile.BlockReason.PackageDetails workHandle
        with e ->
            raise <| Exception (sprintf "Unable to retrieve package details for '%O'-%s" details.Package.PackageName details.Version.AsString, e)

    let startedGetVersionsRequests = System.Collections.Concurrent.ConcurrentDictionary<_,ResolverTaskMemory<_>>()
    let startRequestGetVersions (versions:GetPackageVersionsParameters) =
        let key = (versions.Package.Sources, versions.Package.PackageName)
        startedGetVersionsRequests.GetOrAdd (key, fun _ ->
            workerQueue
            |> ResolverRequestQueue.addWork WorkPriority.BackgroundWork (fun ct ->
                getVersionsRaw versions
                |> fun a -> Async.StartAsTaskProperCancel(a, cancellationToken = ct))
            |> ResolverTaskMemory.ofWork)

    let getVersionsBlock resolverStrategy versionParams =
        let workHandle = startRequestGetVersions versionParams
        let versions =
            try getAndReport versionParams.Package.Sources Profile.BlockReason.GetVersion workHandle |> Seq.toList
            with e ->
                raise <| Exception (sprintf "Unable to retrieve package versions for '%O'" versionParams.Package.PackageName, e)
        let sorted =
            match resolverStrategy with
            | ResolverStrategy.Max -> List.sortDescending versions
            | ResolverStrategy.Min -> List.sort versions
        let pref = getPreferredVersionsRaw resolverStrategy versionParams
        pref @ sorted |> List.toSeq

    let packageFilter =
        match updateMode with
        | UpdateFiltered (g, f) when g = groupName -> Some f
        | _ -> None

    let rootSettings =
        rootDependencies
        |> Seq.map (fun x -> x.Name,x.Settings)
        |> dict

    let lockedPackages =
        rootDependencies
        |> Seq.choose (fun d ->
            match d.VersionRequirement with
            | VersionRequirement.VersionRequirement(VersionRange.OverrideAll v, _) -> Some (d.Name)
            | _ -> None)
        |> Set.ofSeq


    if Set.isEmpty rootDependencies then Resolution.ofRaw [] (ResolutionRaw.OkRaw Map.empty)
    else

    /// Evaluates whethere the innermost step-looping stage should continue or not
    let keepLooping (flags:StepFlags) (conflictState:ConflictState) (currentRequirement:PackageRequirement) =
        if flags.ForceBreak then false else
        if conflictState.Status.IsDone then false else
        if Seq.isEmpty conflictState.VersionsToExplore then
            // TODO: maybe this is the wrong place to report this...
            //match conflictState.Status with
            //| Resolution.Ok _ -> ()
            //| _ -> (tracefn "   Failed to satisfy %O" currentRequirement)
            false else
        flags.FirstTrial || Set.isEmpty conflictState.Conflicts

    let rec step (stage:Stage) (stackpack:StackPack) compatibleVersions (flags:StepFlags) =

        let inline fuseConflicts currentConflict priorConflictSteps conflicts =
            let findMatchingStep priorConflictSteps =
                let currentNames =
                    conflicts
                    |> Seq.map (fun c ->
                        c.Graph
                        |> Set.map (fun (pr:PackageRequirement) -> pr.Name) 
                        |> Set.add c.Name)
                    |> Set.unionMany
                priorConflictSteps
                |> List.tryExtractOne (fun (_,_,lastRequirement:PackageRequirement,_,_) ->
                    currentNames |> Set.contains lastRequirement.Name)

            match findMatchingStep priorConflictSteps with
            | None, []  -> currentConflict
            | (Some head), priorConflictSteps ->
                let (lastConflict, lastStep, lastRequirement, lastCompatibleVersions, lastFlags) = head
                let continueConflict = 
                    { currentConflict with VersionsToExplore = lastConflict.VersionsToExplore }        
                step (Inner((continueConflict,lastStep,lastRequirement), priorConflictSteps))  stackpack lastCompatibleVersions lastFlags
            // could not find a specific package - go back one step
            | None, head :: priorConflictSteps ->
                let (lastConflict, lastStep, lastRequirement, lastCompatibleVersions, lastFlags) = head
                let continueConflict = 
                    { currentConflict with VersionsToExplore = lastConflict.VersionsToExplore }        
                step (Inner((continueConflict,lastStep,lastRequirement), priorConflictSteps))  stackpack lastCompatibleVersions lastFlags
                        
        match stage with            
        | Step((currentConflict,currentStep,_currentRequirement), priorConflictSteps)  -> 
            if Set.isEmpty currentStep.OpenRequirements then
                let currentConflict =
                    { currentConflict with
                        Status = ResolutionRaw.OkRaw (cleanupNames currentStep.CurrentResolution) }
              
                match currentConflict, priorConflictSteps with
                | currentConflict, (lastConflict,lastStep,lastRequirement,lastCompatibleVersions,lastFlags)::priorConflictSteps -> 
                    let continueConflict = {
                        currentConflict with
                            VersionsToExplore = lastConflict.VersionsToExplore
                    }        
                    match continueConflict.Status with
                    | ResolutionRaw.ConflictRaw { RequirementSet = conflicts }
                        when
                            (Set.isEmpty conflicts |> not)
                            && currentStep.CurrentResolution.Count > 1
                            && not (conflicts |> Set.exists (fun r ->
                                r = lastRequirement
                                || r.Graph |> Set.contains lastRequirement)) ->
                       
                        step (Inner((continueConflict,lastStep,lastRequirement),priorConflictSteps)) stackpack lastCompatibleVersions  { flags with ForceBreak = true } 
                    | _ ->
                        step (Inner((continueConflict,lastStep,lastRequirement),priorConflictSteps)) stackpack lastCompatibleVersions  lastFlags 
                
                | currentConflict, [] -> currentConflict

            else
                if Logging.verbose then
                    verbosefn "   %d packages in resolution.%s\n   %d requirements left%s\n" 
                        currentStep.CurrentResolution.Count 
                        (currentStep.CurrentResolution |> Seq.map (fun x -> sprintf "\n     - %O, %O" x.Key x.Value.Version) |> String.Concat)
                        currentStep.OpenRequirements.Count
                        (currentStep.OpenRequirements  |> Seq.map (fun x -> sprintf "\n     - %O, %O (from %O)" x.Name x.VersionRequirement x.Parent) |> String.Concat)

                let currentRequirement = 
                    getCurrentRequirement packageFilter currentStep.OpenRequirements stackpack.ConflictHistory

                let conflicts = 
                    getConflicts currentStep currentRequirement stackpack.KnownConflicts

                let currentConflict =
                    let getVersionsF packName =
                        getVersionsBlock ResolverStrategy.Max (GetPackageVersionsParameters.ofParams currentRequirement.Sources groupName packName)
                    if Seq.isEmpty conflicts then
                        { currentConflict with
                            Status = ResolutionRaw.ConflictRaw {
                                ResolveStep = currentStep
                                RequirementSet = Set.empty
                                Requirement = currentRequirement
                                GetPackageVersions = getVersionsF
                            }}
                    else
                        { currentConflict with
                            Status = ResolutionRaw.ConflictRaw {
                                ResolveStep = currentStep
                                RequirementSet = set conflicts
                                Requirement = Seq.head conflicts
                                GetPackageVersions = getVersionsF }}

                if not (Seq.isEmpty conflicts) then                
                    fuseConflicts currentConflict priorConflictSteps conflicts
                else
                    let compatibleVersions,globalOverride,tryRelaxed =
                        getCompatibleVersions currentStep groupName currentRequirement getVersionsBlock
                                currentConflict.GlobalOverride
                                globalStrategyForDirectDependencies
                                globalStrategyForTransitives

                    let currentConflict = {
                        currentConflict with
                            Conflicts           = set conflicts
                            TryRelaxed          = tryRelaxed
                            GlobalOverride      = globalOverride
                    }
                    let conflictState, stackpack =
                        if Seq.isEmpty compatibleVersions then
                            boostConflicts currentStep.FilteredVersions currentRequirement stackpack currentConflict
                        else
                            currentConflict, stackpack
                    let flags = { 
                      flags with
                        Ready       = false
                        UseUnlisted = false
                        HasUnlisted = false
                        UnlistedSearch = false
                    }
                    step (Outer ((conflictState,currentStep,currentRequirement),priorConflictSteps)) stackpack compatibleVersions  flags 
        | Outer ((currentConflict,currentStep,currentRequirement), priorConflictSteps) ->
            if flags.Ready then
                fuseConflicts currentConflict priorConflictSteps (HashSet [ currentRequirement ])
            else
                let flags = {
                  flags with
                    ForceBreak = false 
                    FirstTrial = true
                }
                let currentConflict = { currentConflict with VersionsToExplore = compatibleVersions }
                step (Inner ((currentConflict,currentStep,currentRequirement), priorConflictSteps)) stackpack compatibleVersions  flags 

        | Inner ((currentConflict,currentStep,currentRequirement), priorConflictSteps)->
            if not (keepLooping flags currentConflict currentRequirement) then
                let flags =
                    if  not flags.UseUnlisted 
                     && flags.HasUnlisted 
                     && not flags.UnlistedSearch
                     && not currentConflict.Status.IsDone 
                     then
                     // if it's been determined that an unlisted package must be used, ready must be set to false
                        if verbose then
                            verbosefn "\nSearching for compatible unlisted package\n"
                        { flags with 
                            Ready = false
                            UseUnlisted = true
                            UnlistedSearch = true
                        }
                    else
                        { flags with 
                            Ready = true 
                            UnlistedSearch = true 
                        }
                step (Outer((currentConflict,currentStep,currentRequirement), priorConflictSteps)) stackpack compatibleVersions  flags 
            else
                let flags = { flags with FirstTrial = false }
                let ({ Version = version; Sources = sources }: VersionCache) & versionToExplore = Seq.head currentConflict.VersionsToExplore

                let currentConflict = 
                    { currentConflict with VersionsToExplore = Seq.tail currentConflict.VersionsToExplore }

                let packageConfig = {
                    GroupName          = groupName
                    Dependency         = currentRequirement
                    GlobalRestrictions = globalFrameworkRestrictions
                    RootSettings       = rootSettings
                    Version            = version
                    Sources            = sources
                    UpdateMode         = updateMode
                    CliTools           = cliToolSettings
                }

                match getExploredPackage packageConfig getPackageDetailsBlock stackpack with
                | stackpack, Result.Error err ->
                    step (Inner((currentConflict.AddError err.SourceException,currentStep,currentRequirement), priorConflictSteps)) stackpack compatibleVersions flags

                | stackpack, Result.Ok(alreadyExplored,exploredPackage) ->
                    let hasUnlisted = exploredPackage.Unlisted || flags.HasUnlisted
                    let flags = { flags with HasUnlisted = hasUnlisted }

                    // Start pre-loading infos about dependencies.
                    for (pack,verReq,restr) in exploredPackage.Dependencies do
                        async {
                            let requestVersions = startRequestGetVersions (GetPackageVersionsParameters.ofParams currentRequirement.Sources groupName pack)
                            requestVersions.Work.TryReprioritize true WorkPriority.LikelyRequired
                            let! versions = (requestVersions).Work.Task |> Async.AwaitTask
                            // Preload the first version in range of this requirement
                            for (verToPreload, prio) in selectVersionsToPreload verReq (versions |> Seq.map fst) do
                                let w = startRequestGetPackageDetails (GetPackageDetailsParameters.ofParams currentRequirement.Sources groupName pack verToPreload)
                                w.Work.TryReprioritize true prio
                            return ()
                        } |> Async.Start

                    if exploredPackage.Unlisted && not flags.UseUnlisted then
                        if not alreadyExplored then
                            tracefn "     %O %O was unlisted" exploredPackage.Name exploredPackage.Version
                        step (Inner ((currentConflict,currentStep,currentRequirement), priorConflictSteps)) stackpack compatibleVersions flags 
                    else
                        // It might be that this version is already not possible because of our current set.
                        // Example: We took A with version 1.0.0 (in our current resolution), but this version depends on A > 1.0.0
                        let conflictingResolvedPackages =
                            currentStep.CurrentResolution
                            |> Map.toSeq
                            |> Seq.map snd
                            // Ignore packages which have "OverrideAll", otherwise == will not work anymore.
                            |> Seq.filter (fun resolved -> lockedPackages.Contains resolved.Name |> not)
                            |> Seq.choose (fun r -> 
                                DependencySetFilter.findFirstIncompatibility currentStep exploredPackage.Dependencies r
                                |> Option.map (fun incompat -> r,incompat))

                        let canTakePackage = conflictingResolvedPackages |> Seq.isEmpty

                        if canTakePackage then
                            let nextStep =
                                {   Relax              = currentStep.Relax
                                    FilteredVersions   = Map.add currentRequirement.Name ([versionToExplore],currentConflict.GlobalOverride) currentStep.FilteredVersions
                                    CurrentResolution  = Map.add exploredPackage.Name exploredPackage currentStep.CurrentResolution
                                    ClosedRequirements = Set.add currentRequirement currentStep.ClosedRequirements
                                    OpenRequirements   = calcOpenRequirements(exploredPackage,globalFrameworkRestrictions,versionToExplore,currentRequirement,currentStep)
                                }
                            if nextStep.OpenRequirements = currentStep.OpenRequirements then
                                failwithf "The resolver confused itself. The new open requirements are the same as the old ones.\n\
                                           This will result in an endless loop.%sCurrent Requirement: %A%sRequirements: %A"
                                                Environment.NewLine currentRequirement Environment.NewLine nextStep.OpenRequirements
                            step (Step((currentConflict,nextStep,currentRequirement), (currentConflict,currentStep,currentRequirement,compatibleVersions,flags)::priorConflictSteps)) stackpack currentConflict.VersionsToExplore flags
                        else
                            let getVersionsF packName =
                                getVersionsBlock ResolverStrategy.Max (GetPackageVersionsParameters.ofParams currentRequirement.Sources groupName packName)
                            let conflictingPackage,(_,vr,_) = conflictingResolvedPackages |> Seq.head
                            let currentConflict =    
                                { currentConflict with
                                    Status = ResolutionRaw.ConflictRaw {
                                        ResolveStep = currentStep
                                        RequirementSet = Set.empty
                                        Requirement = 
                                            { currentRequirement with 
                                                  Name = conflictingPackage.Name
                                                  VersionRequirement = vr
                                                  Parent = Package(currentRequirement.Name,exploredPackage.Version,exploredPackage.Source)  }
                                        GetPackageVersions = getVersionsF }}

                            step (Inner ((currentConflict,currentStep,currentRequirement), priorConflictSteps)) stackpack compatibleVersions flags

    let startingStep = {
        Relax              = false
        FilteredVersions   = Map.empty
        CurrentResolution  = Map.empty
        ClosedRequirements = Set.empty
        OpenRequirements   = rootDependencies
    }

    for openReq in startingStep.OpenRequirements do
        startRequestGetVersions (GetPackageVersionsParameters.ofParams openReq.Sources groupName openReq.Name)
        |> ignore

    let currentRequirement = getCurrentRequirement packageFilter startingStep.OpenRequirements (Dictionary())

    let status =
        let getVersionsF packName = getVersionsBlock ResolverStrategy.Max (GetPackageVersionsParameters.ofParams currentRequirement.Sources groupName packName)
        ResolutionRaw.ConflictRaw { ResolveStep = startingStep; RequirementSet = Set.empty; Requirement = currentRequirement; GetPackageVersions = getVersionsF }


    let currentConflict : ConflictState = {
        Status               = (status : ResolutionRaw)
        Errors               = []
        LastConflictReported = DateTime.Now
        TryRelaxed           = false
        GlobalOverride       = false
        Conflicts            = (Set.empty : Set<PackageRequirement>)
        VersionsToExplore    = (Seq.empty : seq<VersionCache>)
    }

    let stackpack = {
        ExploredPackages     = Dictionary<PackageName*SemVerInfo,ResolvedPackage>()
        KnownConflicts       = (HashSet() : HashSet<HashSet<PackageRequirement> * (VersionCache list * bool) option>)
        ConflictHistory      = (Dictionary() : Dictionary<PackageName, int>)
    }

    let flags = {
        Ready       = false
        UseUnlisted = false
        HasUnlisted = false
        ForceBreak  = false
        FirstTrial  = true
        UnlistedSearch = false
    }

    let inline calculate () = step (Step((currentConflict,startingStep,currentRequirement),[])) stackpack Seq.empty flags

    // Flag to ensure that we don't hide underlying exceptions in the finally block.
    let mutable exceptionThrown = false
    try
#if DEBUG && !DOTNETCORE
        let mutable results = None
        let mutable error = None
        // Increase stack size, because we have no tail-call-elimination
        let thread = new System.Threading.Thread((fun () ->
            try
                results <- Some (calculate())
            with e ->
                // Prevent the application from crashing
                error <- Some (System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture e)
            ), 1024 * 1024 * 100)
        thread.Name <- sprintf "Paket Resolver Thread (Debug) - %O" (System.Guid.NewGuid())
        thread.Start()
        thread.Join()
        match error with
        | Some e -> e.Throw()
        | _ -> ()
        let stepResult =
            match results with
            | Some s -> s
            | None -> failwithf "Expected to get results from the resolver thread :/."
#else
        let stepResult = calculate()
#endif
        let state =
            match stepResult with
            | { Status = ResolutionRaw.ConflictRaw _ } as conflict ->
                if conflict.TryRelaxed then
                    stackpack.KnownConflicts.Clear()
                    stackpack.ConflictHistory.Clear()
                    (step (Step((conflict
                                ,{startingStep with Relax=true}
                                ,currentRequirement),[]))
                          stackpack Seq.empty flags)
                else
                    conflict
            | x -> x
        let resolution = Resolution.ofRaw state.Errors state.Status
        if resolution.IsOk && resolution.Errors.Length > 0 then
            // At least warn that the resolution might not contain the latest stuff, because something failed
            traceWarnfn "Resolution finished, but some errors were encountered:"
            AggregateException(resolution.Errors)
                |> printError

        exceptionThrown <- false
        resolution
    finally
        // some cleanup
        cts.Cancel()
        for w in workers do
            try
                w.Wait()
            with
            | :? ObjectDisposedException ->
                if verbose then
                    traceVerbose "Worker-Task was disposed"
                ()
            | :? AggregateException as a ->
                match a.InnerExceptions |> Seq.toArray with
                | [| :? OperationCanceledException as c |] ->
                    // Task was cancelled...
                    if verbose then
                        traceVerbose "Worker-Task was canceled"
                    ()
                | _ ->
                    if exceptionThrown then
                        traceErrorfn "Error while waiting for worker to finish: %O" a
                    else reraise()
            | e when exceptionThrown ->
                traceErrorfn "Error while waiting for worker to finish: %O" e


type PackageInfo =
  { Resolved : ResolvedPackage
    GroupSettings : InstallSettings
    Settings : InstallSettings }
    member x.Name = x.Resolved.Name
    member x.Version = x.Resolved.Version
    member x.Dependencies = x.Resolved.Dependencies
    member x.Unlisted = x.Resolved.Unlisted
    member x.IsRuntimeDependency = x.Resolved.IsRuntimeDependency
    member x.IsCliTool = x.Resolved.IsCliTool
    member x.Source = x.Resolved.Source
    static member from v s =
      { Resolved = v
        GroupSettings = s
        Settings = v.Settings + s }