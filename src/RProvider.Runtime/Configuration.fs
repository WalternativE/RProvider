﻿/// [omit]
module RProvider.Internal.Configuration

open System
open System.IO
open System.Reflection
open System.Configuration
open System.Collections.Generic

/// Returns the Assembly object of RProvider.Runtime.dll (this needs to
/// work when called from RProvider.DesignTime.dll and also RProvider.Server.exe)
let getRProviderRuntimeAssembly() =
  AppDomain.CurrentDomain.GetAssemblies()
  |> Seq.find (fun a -> a.FullName.StartsWith("RProvider.Runtime,"))

/// Finds directories relative to 'dirs' using the specified 'patterns'.
/// Patterns is a string, such as "..\foo\*\bar" split by '\'. Standard
/// .NET libraries do not support "*", so we have to do it ourselves..
let rec searchDirectories patterns dirs = 
  match patterns with 
  | [] -> dirs
  | "*"::patterns ->
      dirs |> List.collect (Directory.GetDirectories >> List.ofSeq)
      |> searchDirectories patterns
  | name::patterns -> 
      dirs |> List.map (fun d -> Path.Combine(d, name))
      |> searchDirectories patterns

/// Returns the real assembly location - when shadow copying is enabled, this
/// returns the original assembly location (which may contain other files we need)
let getAssemblyLocation (assem:Assembly) = 
  if System.AppDomain.CurrentDomain.ShadowCopyFiles then
      (new System.Uri(assem.EscapedCodeBase)).LocalPath
  else assem.Location

/// Reads the 'RProvider.dll.config' file and gets the 'ProbingLocations' 
/// parameter from the configuration file. Resolves the directories and returns
/// them as a list.
let getProbingLocations() = 
  try
    let root = getRProviderRuntimeAssembly() |> getAssemblyLocation
    let config = System.Configuration.ConfigurationManager.OpenExeConfiguration(root)
    let pattern = config.AppSettings.Settings.["ProbingLocations"]
    if pattern <> null then
      [ let pattern = pattern.Value.Split(';', ',') |> List.ofSeq
        for pat in pattern do 
          let roots = [ Path.GetDirectoryName(root) ]
          for dir in roots |> searchDirectories (List.ofSeq (pat.Split('/','\\'))) do
            if Directory.Exists(dir) then yield dir ]
    else []
  with :? ConfigurationErrorsException | :? KeyNotFoundException -> []


/// Given an assembly name, try to find it in either assemblies
/// loaded in the current AppDomain, or in one of the specified 
/// probing directories.
let resolveReferencedAssembly (asmName:string) = 
  
  // Do not interfere with loading FSharp.Core resources, see #97
  // This also breaks for "mscorlib.resources" and so it might be good idea to skip all 
  // resources (both short format "foo.resources" and long format "foo.resources, Version=4.0.0.0...")
  if asmName.EndsWith ".resources" || asmName.Contains ".resources," then 
    (* Do not log when we skip, because that would cause recursive lookup for mscorlib.resources *) null else
  Logging.logf "Attempting resolution for '%s'" asmName

  // First, try to find the assembly in the currently loaded assemblies
  let fullName = AssemblyName(asmName)
  let loadedAsm = 
    System.AppDomain.CurrentDomain.GetAssemblies()
    |> Seq.tryFind (fun a -> AssemblyName.ReferenceMatchesDefinition(fullName, a.GetName()))
  match loadedAsm with
  | Some asm -> asm
  | None ->

    // Otherwise, search the probing locations for a DLL file
    let libraryName = 
      let idx = asmName.IndexOf(',') 
      if idx > 0 then asmName.Substring(0, idx) else asmName

    let locations = getProbingLocations()
    Logging.logf "Probing locations: %s" (String.concat ";" locations)

    let asm = locations |> Seq.tryPick (fun dir ->
      let library = Path.Combine(dir, libraryName+".dll")
      if File.Exists(library) then
        Logging.logf "Found assembly, checking version! (%s)" library
        // We do a ReflectionOnlyLoad so that we can check the version
        let refAssem = Assembly.ReflectionOnlyLoadFrom(library)
        // If it matches, we load the actual assembly
        if refAssem.FullName = asmName then 
          Logging.logf "...version matches, returning!"
          Some(Assembly.LoadFrom(library)) 
        else 
          Logging.logf "...version mismatch, skipping"
          None
      else None)
             
    if asm = None then Logging.logf "Assembly not found!"
    defaultArg asm null

let isUnixOrMac () = 
    let platform = Environment.OSVersion.Platform 
    // The guide at www.mono-project.com/FAQ:_Technical says to also check for the
    // value 128, but that is only relevant to old versions of Mono without F# support
    platform = PlatformID.MacOSX || platform = PlatformID.Unix              

/// On Mac (and Linux), we use ~/.rprovider.conf in user's home folder for 
/// various configuration (64-bit mono and R location if we cannot determine it)
let getRProviderConfValue key = 
    Logging.logf "getRProviderConfValue '%s'" key
    if isUnixOrMac() then
        let home = Environment.GetEnvironmentVariable("HOME")
        try
            Logging.logf "getRProviderConfValue - Home: '%s'" home
            let config = home + "/.rprovider.conf"
            IO.File.ReadLines(config) 
            |> Seq.tryPick (fun line ->
                match line.Split('=') with
                | [| key'; value |]  when key' = key -> Some value
                | _ -> None )
        with _ -> None
    else None
