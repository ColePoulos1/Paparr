using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Paparr.API.Authentication;

public static class OwnerPassword
{
    private const int Iterations = 210_000;
    private static readonly PasswordHasher<string> Hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = Iterations
    }));

    public static string Hash(string password)
    {
        if (password.Length is < 16 or > 512)
            throw new ArgumentException("Use a password between 16 and 512 characters.", nameof(password));
        return Hasher.HashPassword("owner", password);
    }

    public static bool Verify(string hash, string password) =>
        password.Length is > 0 and <= 512 && IsValidHash(hash) &&
        Hasher.VerifyHashedPassword("owner", hash, password) != PasswordVerificationResult.Failed;

    public static string Stamp(string hash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash)));

    public static bool IsValidHash(string hash)
    {
        Span<byte> bytes = stackalloc byte[256];
        if (!Convert.TryFromBase64String(hash, bytes, out var length) || length < 61 || bytes[0] != 1) return false;
        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes[1..5]);
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes[5..9]);
        var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[9..13]);
        return prf == 2 && iterations is >= Iterations and <= 1_000_000 &&
            saltLength >= 16 && saltLength <= length - 45;
    }

    public static int RunHashCommand()
    {
        try
        {
            string password;
            if (Console.IsInputRedirected)
            {
                password = Console.ReadLine() ?? "";
            }
            else
            {
                Console.Error.Write("Password (16–512 characters): ");
                password = ReadHidden();
                Console.Error.Write("Confirm password: ");
                if (password != ReadHidden()) throw new ArgumentException("Passwords did not match.");
            }
            Console.WriteLine(Hash(password));
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string ReadHidden()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
            else if (!char.IsControl(key.KeyChar) && value.Length < 513) value.Append(key.KeyChar);
        }
    }
}
