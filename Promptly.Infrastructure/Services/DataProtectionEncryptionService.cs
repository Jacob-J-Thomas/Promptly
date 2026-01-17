using Microsoft.AspNetCore.DataProtection;
using Promptly.Application.Interfaces;

namespace Promptly.Infrastructure.Services;

public class DataProtectionEncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;

    public DataProtectionEncryptionService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("Promptly.SensitiveData");
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return plainText;
        }

        return _protector.Protect(plainText);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return cipherText;
        }

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to decrypt data. The encryption key may have changed.", ex);
        }
    }
}
