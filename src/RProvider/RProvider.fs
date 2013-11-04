﻿namespace RProvider

open System
open System.Collections.Generic
open System.Reflection
open System.IO
open System.Diagnostics
open System.Threading
open Samples.FSharp.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices
open RDotNet
open RInteropInternal
open RInterop
open Microsoft.Win32
open System.IO

module internal ProviderUtils =
    // Have to be careful that this code is in its own module
    // If it is in some other module, which might be initialized before the PATH is set, we will get initialization exceptions
    let rLocation =
        let locateRfromRegistry () =
            let rCore =
                match Registry.LocalMachine.OpenSubKey @"SOFTWARE\R-core", Registry.CurrentUser.OpenSubKey @"SOFTWARE\R-core" with
                | null, null -> failwithf "Reg key Software\R-core does not exist; R is likely not installed on this computer"
                | null, x -> x
                | x, _ -> x
            
            let key = rCore.OpenSubKey "R"
            if key = null then
                failwith "SOFTWARE\R-core exists but subkey R does not exist"

            key.GetValue "InstallPath" |> unbox

        match Environment.GetEnvironmentVariable "R_HOME" with
        | null -> locateRfromRegistry()
        | rPath -> rPath 


[<TypeProvider>]
type public RProvider(cfg:TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces()

    // R potentially may be not installed - handle this in static constructor for improved diag (G.B.)
    static do
      try
        Logging.logf "Rprovider cctor"
        let binPath = Path.Combine(ProviderUtils.rLocation, "bin", if Environment.Is64BitProcess then "x64" else "i386")
        if not (Path.Combine(binPath, "R.dll") |> File.Exists) then
            failwithf "No R engine at %s" binPath

        // Set the path
        Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH") + ";" + binPath)
        Logging.logf "Rprovider cctor completed"
      with e ->
        Logging.logf "Rprovider cctor failed: %O" e
        //System.Diagnostics.Debugger.Break()
        System.Diagnostics.Debug.Write(e)
        reraise()

    // Get the assembly and namespace used to house the provided types
    do
    try
      Logging.logf "Rprovider ctor starting"
      let asm = System.Reflection.Assembly.GetExecutingAssembly()
      let ns = "RProvider"

      // Expose all available packages as namespaces
      do
          Logging.logf "Rprovider ctor getting packages"
          for package in getPackages() do
              let pns = ns + "." + package
              let pty = ProvidedTypeDefinition(asm, pns, "R", Some(typeof<obj>))    

              pty.AddXmlDocDelayed <| fun () -> getPackageDescription package
              pty.AddMembersDelayed( fun () -> 
                [ loadPackage package
                  let bindings = getBindings package

                  // We get the function descriptions for R the first time they are needed
                  let titles = lazy getFunctionDescriptions package

                  for name, rval in Map.toSeq bindings do
                      let memberName = makeSafeName name
                      match rval with
                      | RValue.Function(paramList, hasVarArgs) ->
                          let paramList = [ for p in paramList -> 
                                                  ProvidedParameter(makeSafeName p,  typeof<obj>, optionalValue=null)

                                            if hasVarArgs then
                                              yield ProvidedParameter("paramArray", typeof<obj[]>, optionalValue=null, isParamArray=true)
                                          ]
                        
                          let paramCount = paramList.Length
                        
                          let pm = ProvidedMethod(
                                       methodName = memberName,
                                       parameters = paramList,
                                       returnType = typeof<SymbolicExpression>,
                                       IsStaticMethod = true,
                                       InvokeCode = fun args -> if args.Length <> paramCount then
                                                                  failwithf "Expected %d arguments and received %d" paramCount args.Length
                                                 
                                                                if hasVarArgs then
                                                                  let namedArgs = 
                                                                      Array.sub (Array.ofList args) 0 (paramCount-1)
                                                                      |> List.ofArray
                                                                  let namedArgs = Quotations.Expr.NewArray(typeof<obj>, namedArgs)
                                                                  let varArgs = args.[paramCount-1]
                                                                  <@@ RInterop.call package name %%namedArgs %%varArgs @@>                                                 
                                                                else
                                                                  let namedArgs = Quotations.Expr.NewArray(typeof<obj>, args)                                            
                                                                  <@@ RInterop.call package name %%namedArgs [||] @@> )

                          pm.AddXmlDocDelayed (fun () -> match titles.Value.TryFind name with 
                                                         | Some docs -> docs 
                                                         | None -> "No documentation available")                                    
                        
                          yield pm :> MemberInfo

                          // Yield an additional overload that takes a Dictionary<string, object>
                          // This variant is more flexible for constructing lists, data frames etc.
                          let pdm = ProvidedMethod(
                                       methodName = memberName,
                                       parameters = [ ProvidedParameter("paramsByName",  typeof<IDictionary<string,obj>>) ],
                                       returnType = typeof<SymbolicExpression>,
                                       IsStaticMethod = true,
                                       InvokeCode = fun args -> if args.Length <> 1 then
                                                                  failwithf "Expected 1 argument and received %d" args.Length
                                                                let argsByName = args.[0]
                                                                <@@   let vals = %%argsByName: IDictionary<string,obj>
                                                                      let valSeq = vals :> seq<KeyValuePair<string, obj>>
                                                                      RInterop.callFunc package name valSeq null @@> )
                          yield pdm :> MemberInfo                                    
                      | RValue.Value ->
                          yield ProvidedProperty(
                                  propertyName = memberName,
                                  propertyType = typeof<SymbolicExpression>,
                                  IsStatic = true,
                                  GetterCode = fun _ -> <@@ RInterop.call package name [| |] [| |] @@>) :> MemberInfo  ] )
                      
              this.AddNamespace(pns, [ pty ])
          Logging.logf "Rprovider ctor finished"

    with e ->
      Logging.logf "Rprovider ctor failed: %O" e
      //System.Diagnostics.Debugger.Break()
      System.Diagnostics.Debug.Write(e)
      reraise()


[<TypeProviderAssembly>]
do()