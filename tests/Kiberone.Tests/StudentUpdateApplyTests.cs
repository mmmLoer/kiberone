using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class StudentUpdateApplyTests
{
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
