using Kiberone.Core;
using Kiberone.Infrastructure;
namespace Kiberone.Tests;
public sealed class LearningStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-learning-test-" + Guid.NewGuid().ToString("N"));
    private readonly Guid child = Guid.NewGuid();
    private readonly LearningStore learning;
    public LearningStoreTests()
    {
        var secret = LocationPassword.Create("test-password");
        var hub = new ClassroomHubStore(root, [new LocationSecretRecord("A", secret.Salt, secret.Hash), new LocationSecretRecord("B", secret.Salt, secret.Hash)]);
        var group = Guid.NewGuid();
        hub.Put("A", "test-password", new LocationRosterSnapshot("A", DateTimeOffset.UtcNow,
            [new LocationGroupSnapshot(group,"Group","","","A",[])],
            [new LocationStudentSnapshot(child,"Test","Child",10,null,group,"","","",0,0)]));
        learning = new LearningStore(Path.Combine(root,"learning"),hub);
    }
    [Fact] public void LibraryIsSharedButOnlyOwnerCanPublishAndRevisionIsChecked()
    {
        var document = new QuizDocument { Title = "Shared", Questions = [new QuizDocumentQuestion { Text = "Question?", Options = ["A","B"], CorrectIndex = 1 }] };
        var saved = learning.Publish(new PublishQuizRequest("A","test-password",document));
        Assert.Single(learning.List(new LearningAuth("B","test-password")));
        Assert.Throws<UnauthorizedAccessException>(() => learning.Publish(new PublishQuizRequest("B","test-password",document,1)));
        Assert.Throws<LearningConflictException>(() => learning.Publish(new PublishQuizRequest("A","test-password",document,0)));
        Assert.Equal(2, learning.Publish(new PublishQuizRequest("A","test-password",document,saved.Revision)).Revision);
        Assert.Throws<UnauthorizedAccessException>(() => learning.List(new LearningAuth("A","wrong")));
    }
    [Fact] public void RecordsMergeAcrossComputersWithoutReducingEitherRecord()
    {
        var key = TypingRecordKey.For("Lesson","Text");
        learning.Records(new TypingRecordsRequest("A","test-password",child,[new(key,100,95,100)]));
        var result = learning.Records(new TypingRecordsRequest("A","test-password",child,[new(key,80,99,120)])).Single();
        Assert.Equal(100,result.Speed); Assert.Equal(99,result.Accuracy);
        Assert.Throws<UnauthorizedAccessException>(() => learning.Records(new TypingRecordsRequest("B","test-password",child)));
        Assert.Throws<UnauthorizedAccessException>(() => learning.Records(new TypingRecordsRequest("A","test-password",Guid.NewGuid())));
    }
    [Fact] public void RecordsBefore100CorrectCharactersAreRejected()
    {
        var key=TypingRecordKey.For("Lesson","Text");
        Assert.Throws<ArgumentException>(() => learning.Records(new TypingRecordsRequest("A","test-password",child,[new(key,100,100,99)])));
        Assert.Empty(learning.Records(new TypingRecordsRequest("A","test-password",child)));
    }
    public void Dispose() { if(Directory.Exists(root)) Directory.Delete(root,true); }
}
