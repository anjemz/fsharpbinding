
// --------------------------------------------------------------------------------------
// Common utilities for environment, debugging, reflection
// --------------------------------------------------------------------------------------

namespace MonoDevelop.FSharp

open System
open System.IO
open System.Reflection
open System.Globalization
open Microsoft.FSharp.Reflection
open MonoDevelop.Projects
open MonoDevelop.Ide.Gui
open MonoDevelop.Ide
open MonoDevelop.Core.Assemblies
open MonoDevelop.Core

/// Implements the (?) operator that makes it possible to access internal methods
/// and properties and contains definitions for F# assemblies
module Reflection =   
  // Various flags configurations for Reflection
  let staticFlags = BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Static 
  let instanceFlags = BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Instance
  let ctorFlags = instanceFlags
  let inline asMethodBase(a:#MethodBase) = a :> MethodBase
  
  let (?) (o:obj) name : 'R =
    // The return type is a function, which means that we want to invoke a method
    if FSharpType.IsFunction(typeof<'R>) then
      let argType, resType = FSharpType.GetFunctionElements(typeof<'R>)
      FSharpValue.MakeFunction(typeof<'R>, fun args ->
        // We treat elements of a tuple passed as argument as a list of arguments
        // When the 'o' object is 'System.Type', we call static methods
        let methods, instance, args = 
          let args = 
            if argType = typeof<unit> then [| |]
            elif not(FSharpType.IsTuple(argType)) then [| args |]
            else FSharpValue.GetTupleFields(args)
          if (typeof<System.Type>).IsAssignableFrom(o.GetType()) then 
            let methods = (unbox<Type> o).GetMethods(staticFlags) |> Array.map asMethodBase
            let ctors = (unbox<Type> o).GetConstructors(ctorFlags) |> Array.map asMethodBase
            Array.concat [ methods; ctors ], null, args
          else 
            o.GetType().GetMethods(instanceFlags) |> Array.map asMethodBase, o, args
        
        // A simple overload resolution based on the name and number of parameters only
        let methods = 
          [ for m in methods do
              if m.Name = name && m.GetParameters().Length = args.Length then yield m ]
        match methods with 
        | [] -> failwithf "No method '%s' with %d arguments found" name args.Length
        | _::_::_ -> failwithf "Multiple methods '%s' with %d arguments found" name args.Length
        | [:? ConstructorInfo as c] -> c.Invoke(args)
        | [ m ] -> m.Invoke(instance, args) ) |> unbox<'R>
    else
      // When the 'o' object is 'System.Type', we access static properties
      let typ, flags, instance = 
        if (typeof<System.Type>).IsAssignableFrom(o.GetType()) then unbox o, staticFlags, null
        else o.GetType(), instanceFlags, o
      
      // Find a property that we can call and get the value
      let prop = typ.GetProperty(name, flags)
      if prop = null && instance = null then 
        // Try nested type...
        let nested = typ.Assembly.GetType(typ.FullName + "+" + name)
        if nested = null then 
          failwithf "Property or nested type '%s' not found in '%s' using flags '%A'." name typ.Name flags
        elif not ((typeof<'R>).IsAssignableFrom(typeof<System.Type>)) then
          failwithf "Cannot return nested type '%s' as value of type '%s'." nested.Name (typeof<'R>.Name)
        else nested |> box |> unbox<'R>
      else
        // Call property
        let meth = prop.GetGetMethod(true)
        if prop = null then failwithf "Property '%s' found, but doesn't have 'get' method." name
        try meth.Invoke(instance, [| |]) |> unbox<'R>
        with err -> failwithf "Failed to get value of '%s' property (of type '%s'), error: %s" name typ.Name (err.ToString())

module Environment = 
  /// Are we running on the Mono platform?
  let runningOnMono = 
    try System.Type.GetType("Mono.Runtime") <> null
    with _ -> false        


