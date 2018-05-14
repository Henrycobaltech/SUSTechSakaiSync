﻿open System
open System.Xml.Linq
open System.IO
open System.Net
open WebDav
open System.Linq

type ConfigResource(xmlElement:XElement) = 
    member this.ServerRoot = xmlElement.Attribute("ServerRoot" |> XName.Get).Value
    member this.LocalRoot = xmlElement.Attribute("LocalRoot" |> XName.Get).Value
    member this.Excludes = match xmlElement.Nodes() |> Seq.cast<XElement> |> List.ofSeq with
                            | [] -> Seq.empty
                            | n::ns -> n.Nodes() |> Seq.cast<XElement> |> Seq.map(fun x -> x.Value)

type SyncConfig(xmlElement:XElement) = 
    member this.UserName = xmlElement.Attribute("UserName" |> XName.Get).Value
    member this.Password = xmlElement.Attribute("Password" |> XName.Get).Value
    member this.Resources = xmlElement.Nodes() |> Seq.cast<XElement> |> Seq.map(ConfigResource)

[<AllowNullLiteral>]
type LocalFileInfo(path:string, relativePath:string, lastModified: DateTime) = 
    member this.Path = path
    member this.RelativePath = relativePath
    member this.LastModified = lastModified

type ServerFileInfo(uri:string, relativePath:string, localRoot:string, lastModified: DateTime) = 
    member this.Uri = uri
    member this.RelativePath = relativePath
    member this.LocaRoot = localRoot
    member this.LastModified = lastModified

let getDirectoryFiles path = Directory.GetFiles(path ,"*", SearchOption.AllDirectories)

let getLocalFiles (conf:SyncConfig) = 
    conf.Resources 
    |> Seq.collect(fun r -> r.LocalRoot 
                            |> getDirectoryFiles 
                            |> Seq.map(fun p -> (p, p.[r.LocalRoot.Length..]) )
                  )
    |> Seq.map(fun (p, r) -> (FileInfo p, r) )
    |> Seq.map(fun (fi, r) -> (fi.FullName, r, fi.LastWriteTime) )
    |> Seq.map(LocalFileInfo)
    

let sakaiUri = "https://sakai.sustc.edu.cn"

let getServerDirResources (config:SyncConfig) dir = 
    async{
        let credentials = new NetworkCredential(config.UserName, config.Password)
        let param = WebDavClientParams(Credentials = credentials)
        use client = new WebDavClient(param)
        let! response = client.Propfind(sakaiUri + dir) |> Async.AwaitTask
        return response.Resources |> Seq.skip(1)
    }

let shouldExclude (excl:seq<string>) = 
    fun (x:string) -> excl |> Seq.exists(fun ex -> x.StartsWith ex)

let isWindowsNT = Environment.OSVersion.Platform = PlatformID.Win32NT

let fitOSPathString (uri:string) = 
    match isWindowsNT with
    | true -> uri.Replace("/", "\\")
    | false -> uri

let rec getServerFilesPerSource (config:SyncConfig) (res:ConfigResource) dir = 
    let exclDir = res.Excludes |> Seq.map(fun ex -> res.ServerRoot + ex) |> shouldExclude
    let keepRes = fun (res:WebDavResource) -> res.Uri |> exclDir |> not
    let resources = 
        dir 
        |> getServerDirResources config 
        |> Async.RunSynchronously 
        |> Seq.filter(keepRes)

    let rootLen = sakaiUri + res.ServerRoot |> String.length
    let files = 
        resources 
        |> Seq.filter(fun r-> r.IsCollection |> not) 
        |> Seq.map(fun r -> 
                    let uri = sakaiUri + r.Uri |> Uri.UnescapeDataString
                    (uri, uri.[rootLen..] |> fitOSPathString, res.LocalRoot, r.LastModifiedDate.Value) )
        |> Seq.map(ServerFileInfo)

    let filesInSubDirs = 
        resources 
        |> Seq.filter(fun r-> r.IsCollection) 
        |> Seq.collect(fun r -> getServerFilesPerSource config res r.Uri) 
    
    Seq.concat [ files; filesInSubDirs ]

let getServerFiles (config:SyncConfig) = 
    config.Resources |> Seq.collect(fun r -> getServerFilesPerSource config r r.ServerRoot)

let syncFile (uri:string) (localPath:string) (config:SyncConfig) = 
    async {
        printfn "%s -> %s" uri localPath
        let credentials = new NetworkCredential(config.UserName, config.Password)
        let param = WebDavClientParams(Credentials = credentials)
        use client = new WebDavClient(param)
        let! response = client.GetRawFile(uri) |> Async.AwaitTask
        localPath |> Path.GetDirectoryName |> Directory.CreateDirectory |> ignore
        use fs = new FileStream(localPath, FileMode.OpenOrCreate, FileAccess.Write)
        response.Stream.CopyToAsync fs |> Async.AwaitTask |> ignore
    }
    

[<EntryPoint>]
let main argv =
    let config  = File.ReadAllText("SyncConfig.xml") |> XElement.Parse |> SyncConfig
    let localFiles = config |> getLocalFiles
    let serverFiles = config |> getServerFiles
    let syncItems = 
        query {
            for sf in serverFiles do
            leftOuterJoin lf in localFiles
                on (sf.RelativePath = lf.RelativePath) into result
            for lf in result |> Enumerable.DefaultIfEmpty do
            where(match lf with
                  |null -> true
                  |_ -> sf.LastModified > lf.LastModified)
            select (sf.Uri, sf.LocaRoot+sf.RelativePath)
        } 
        |> Seq.toList
    
    syncItems 
    |> Seq.map(fun (uri, local) -> syncFile uri local config) 
    |> Async.Parallel
    |> Async.RunSynchronously
    |> ignore

    0