using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SingNext.CxlLab;

internal static class DevelopmentIdentity
{
    static string KeyId(ECDsa key)=>Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    static void WriteNew(string path,string text)
    {
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        byte[] bytes=Encoding.UTF8.GetBytes(text);
        try{file.Write(bytes);file.Flush(true);}finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public static object Create(string directory)
    {
        directory=Path.GetFullPath(directory);
        if(Directory.Exists(directory)||File.Exists(directory))throw new IOException("Identity output must be new; never replace an existing signing key.");
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Directory.CreateDirectory(directory);
        WriteNew(Path.Combine(directory,"development-private.pem"),key.ExportPkcs8PrivateKeyPem());
        WriteNew(Path.Combine(directory,"development-public.pem"),key.ExportSubjectPublicKeyInfoPem());
        var metadata=new{SchemaVersion=1,Scope="Reusable development identity; not production provisioning",Curve="nistP384",SpkiSha256=KeyId(key)};
        WriteNew(Path.Combine(directory,"identity.json"),JsonSerializer.Serialize(metadata,LabVm.JsonOptions));
        using var check=Load(directory);
        return new{Directory=directory,metadata.SpkiSha256,PrivateKeyStored=true,ProductionProvisioning=false};
    }
    public static ECDsa Load(string directory)
    {
        directory=Path.GetFullPath(directory);
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"identity.json")));
        var metadata=document.RootElement;
        if(metadata.GetProperty("SchemaVersion").GetInt32()!=1 || metadata.GetProperty("Curve").GetString()!="nistP384")throw new InvalidDataException("Unsupported development identity.");
        var key=ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(Path.Combine(directory,"development-private.pem")));
            var privateParameters=key.ExportParameters(true);
            try{if(privateParameters.Curve.Oid.Value!="1.3.132.0.34"||privateParameters.D is null)throw new InvalidDataException("P384 private signing key required.");}
            finally{if(privateParameters.D is not null)CryptographicOperations.ZeroMemory(privateParameters.D);}
            using var publicKey=ECDsa.Create();publicKey.ImportFromPem(ReadPublic(directory));
            string expected=metadata.GetProperty("SpkiSha256").GetString()??"";
            if(KeyId(key)!=expected||KeyId(publicKey)!=expected)throw new InvalidDataException("Identity public/private/metadata mismatch.");
            return key;
        }
        catch{key.Dispose();throw;}
    }
    public static string ReadPublic(string directory)=>File.ReadAllText(Path.Combine(Path.GetFullPath(directory),"development-public.pem"));
}
