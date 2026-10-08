namespace Kiberone.Core;
public sealed record ProjectCommitInfo(string Sha, string Message, DateTimeOffset Date, string Branch);
public sealed record ProjectBranchRequest(string ClientId, string Branch);
public sealed record ProjectGitRestoreRequest(string ClientId, string Sha);

public sealed record StartQuizDocumentRequest(QuizDocument Document, IReadOnlyList<string> ClientIds);
