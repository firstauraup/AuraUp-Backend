namespace AuraUpBack.Infrastructure.Abstractions;

internal interface IInstagramCredentialVault
{
    string Encrypt(string plainText);
    string Decrypt(string encryptedText);
}
