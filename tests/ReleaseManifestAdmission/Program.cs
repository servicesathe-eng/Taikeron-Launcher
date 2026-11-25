using Taikeron.Launcher.Services;
using System.IO;
var file = "Taikeron-2.404.5.8-Setup-x64.exe";
var url = "https://github.com/Taikeron-CyclingOS/Taikeron-CyclingOS.github.io/releases/download/tl-2.404.5.8/" + file;
var sha = new string('a',64);
var count = 0;
void Pass(Action action) { action(); count++; }
void Reject(Action action) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception("Invalid manifest accepted"); }
Pass(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1.0.0","TL","stable","2.404.5.8","TL"));
Pass(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1.0.0","TMB","stable","0.5.19","TMB"));
Pass(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1","TMC","stable","0.1.2","TMC"));
Reject(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","99","TL","stable","2.404.5.8","TL"));
Reject(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1","TL","stable","2.404.5.8","TL"));
Reject(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1.0.0","TMC","stable","0.1.2","TL"));
Reject(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1.0.0","TL","beta","2.404.5.8","TL"));
Reject(() => ReleaseManifestAdmission.Header("taikeron_release_manifest","1.0.0","TL","stable","bogus","TL"));
Pass(() => ReleaseManifestAdmission.Package(file,url,sha,1));
Reject(() => ReleaseManifestAdmission.Package(file,url,"",1));
Reject(() => ReleaseManifestAdmission.Package(file,url,sha,0));
Reject(() => ReleaseManifestAdmission.Package("../"+file,url,sha,1));
Reject(() => ReleaseManifestAdmission.Package(file,url.Replace("https:","http:"),sha,1));
Reject(() => ReleaseManifestAdmission.Package(file,url.Replace("github.com","example.com"),sha,1));
Reject(() => ReleaseManifestAdmission.Package("different.exe",url,sha,1));
Console.WriteLine($"Release manifest admission: {count} checks passed");
