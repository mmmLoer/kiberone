using System.Text;
namespace Kiberone.Infrastructure;
public static class MailboxName
{
    public static string Create(string lastName, string firstName)
    {
        const string alphabet = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] latin = ["a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya"];
        var result = new StringBuilder();
        foreach (var c in (lastName + firstName).ToLowerInvariant())
        {
            var index = alphabet.IndexOf(c);
            if (index >= 0) result.Append(latin[index]);
            else if (c is >= 'a' and <= 'z' or >= '0' and <= '9') result.Append(c);
        }
        return result.Length == 0 ? "student" : result.ToString()[..Math.Min(result.Length, 55)];
    }
}
