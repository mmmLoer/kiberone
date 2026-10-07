using Kiberone.Infrastructure;
using Kiberone.Vpn;

namespace Kiberone.Tests;

public sealed class StudentUpdateApplyTests
{
    [Fact]
    public void UpdateSourceValidation_AcceptsOnlyStudentPackagesInUserUpdates()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KIBERone Classroom", "updates");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, $"student-test-{Guid.NewGuid():N}.exe");
        var unrelated = Path.Combine(directory, $"other-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(source, "probe");
            File.WriteAllText(unrelated, "probe");
            Assert.True(VpnOptions.IsAllowedUpdateSourcePath(source));
            Assert.False(VpnOptions.IsAllowedUpdateSourcePath(unrelated));
        }
        finally
        {
            File.Delete(source);
            File.Delete(unrelated);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\KIBERone\Student\Kiberone.Student.exe", true)]
    [InlineData(@"C:\Apps\KIBERoneStudent.exe", true)]
    [InlineData(@"C:\Apps\Kiberone.Tutor.exe", false)]
    [InlineData(@"C:\Apps\notepad.exe", false)]
    public void IsStudentExecutablePath_AcceptsInstalledAndLegacyNames(string path, bool expected)
    {
        Assert.Equal(expected, StudentAgent.IsStudentExecutablePath(path));
    }
}
