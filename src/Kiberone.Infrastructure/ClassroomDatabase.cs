using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public static class ClassroomDatabase
{
    public static DbContextOptions<ClassroomDbContext> CreateOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        return new DbContextOptionsBuilder<ClassroomDbContext>()
            .UseSqlite(connectionString)
            .EnableDetailedErrors()
            .Options;
    }

    public static async Task InitializeAsync(DbContextOptions<ClassroomDbContext> options, CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureGamificationSchemaAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);
    }

    public static async Task SeedDefaultsAsync(DbContextOptions<ClassroomDbContext> options, CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        ClassroomGroup? group = await db.Groups.FirstOrDefaultAsync(cancellationToken);
        if (group is null)
        {
            group = new ClassroomGroup { Name = "Python 01", Module = "Python", Topics = "Основы, циклы и функции" };
            db.Groups.Add(group);
            db.Students.AddRange(
                new Student { LastName = "Иванов", FirstName = "Артём", Age = 11, Group = group, Xp = 140, Kiberons = 35 },
                new Student { LastName = "Петрова", FirstName = "Софья", Age = 12, Group = group, Xp = 220, Kiberons = 60 },
                new Student { LastName = "Смирнов", FirstName = "Лев", Age = 10, Group = group, Xp = 80, Kiberons = 20 });
        }

        if (!await db.TypingLessons.AnyAsync(cancellationToken))
        {
            db.TypingLessons.AddRange(
                new TypingLessonTemplate
                {
                    Name = "Разминка: домашний ряд", Description = "Базовая постановка пальцев", ContentKind = LessonContentKind.Letters,
                    KeyboardLayout = "ru-RU", MinimumCharacters = 20, DurationMinutes = 5,
                    Steps = [new TypingLessonStep { Order = 0, Title = "ФЫВА ОЛДЖ", Text = "фыва олдж фыва олдж фыва олдж" }]
                },
                new TypingLessonTemplate
                {
                    Name = "Python: цикл for", Description = "Печать короткого фрагмента кода", ContentKind = LessonContentKind.Code,
                    KeyboardLayout = "en-US", MinimumCharacters = 25, DurationMinutes = 7,
                    Steps = [new TypingLessonStep { Order = 0, Title = "Цикл", Text = "for i in range(10): print(i)" }]
                });
        }

        if (!await db.Achievements.AnyAsync(x => !x.Code.StartsWith("sys_"), cancellationToken))
        {
            db.Achievements.AddRange(
                new Achievement { Code = "accurate_start", Name = "Точный старт", Description = "Первый урок с точностью 90%", XpReward = 30, KiberonReward = 5 },
                new Achievement { Code = "speed_100", Name = "Скорость 100", Description = "Достигнуть скорости 100 CPM", XpReward = 50, KiberonReward = 10 },
                new Achievement { Code = "focus", Name = "Фокус", Description = "Завершить урок без отвлечений", XpReward = 25, KiberonReward = 5 });
        }

        if (!await db.StoreItems.AnyAsync(cancellationToken))
        {
            db.StoreItems.AddRange(
                new StoreItem { Sku = "sticker", Name = "Набор наклеек", Description = "Фирменные наклейки KIBERone", Price = 20, Stock = 20 },
                new StoreItem { Sku = "notebook", Name = "Блокнот", Description = "Блокнот для идей", Price = 50, Stock = 10 },
                new StoreItem { Sku = "headphones", Name = "Наушники", Description = "Цель накопления", Price = 1420, Stock = 3 });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureGamificationSchemaAsync(ClassroomDbContext db, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            """CREATE TABLE IF NOT EXISTS achievements (Id TEXT NOT NULL PRIMARY KEY, Code TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL, Icon TEXT NOT NULL, XpReward INTEGER NOT NULL CHECK (XpReward >= 0), KiberonReward INTEGER NOT NULL CHECK (KiberonReward >= 0), IsActive INTEGER NOT NULL, CreatedAt TEXT NOT NULL);""",
            """CREATE UNIQUE INDEX IF NOT EXISTS IX_achievements_Code ON achievements (Code);""",
            """CREATE TABLE IF NOT EXISTS student_achievements (Id TEXT NOT NULL PRIMARY KEY, StudentId TEXT NOT NULL, AchievementId TEXT NOT NULL, Note TEXT NOT NULL, AwardedAt TEXT NOT NULL, FOREIGN KEY (StudentId) REFERENCES students (Id) ON DELETE CASCADE, FOREIGN KEY (AchievementId) REFERENCES achievements (Id) ON DELETE CASCADE);""",
            """CREATE UNIQUE INDEX IF NOT EXISTS IX_student_achievements_StudentId_AchievementId ON student_achievements (StudentId, AchievementId);""",
            """CREATE TABLE IF NOT EXISTS kiberon_transactions (Id TEXT NOT NULL PRIMARY KEY, StudentId TEXT NOT NULL, Amount INTEGER NOT NULL, BalanceAfter INTEGER NOT NULL, Kind TEXT NOT NULL, Reason TEXT NOT NULL, ReferenceId TEXT NULL, CreatedAt TEXT NOT NULL, FOREIGN KEY (StudentId) REFERENCES students (Id) ON DELETE CASCADE);""",
            """CREATE INDEX IF NOT EXISTS IX_kiberon_transactions_StudentId_CreatedAt ON kiberon_transactions (StudentId, CreatedAt);""",
            """CREATE TABLE IF NOT EXISTS store_items (Id TEXT NOT NULL PRIMARY KEY, Sku TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL, Price INTEGER NOT NULL CHECK (Price >= 0), Stock INTEGER NOT NULL CHECK (Stock >= 0), IsActive INTEGER NOT NULL, IsSecret INTEGER NOT NULL, CreatedAt TEXT NOT NULL);""",
            """CREATE UNIQUE INDEX IF NOT EXISTS IX_store_items_Sku ON store_items (Sku);""",
            """CREATE TABLE IF NOT EXISTS store_orders (Id TEXT NOT NULL PRIMARY KEY, StudentId TEXT NOT NULL, StoreItemId TEXT NOT NULL, PricePaid INTEGER NOT NULL CHECK (PricePaid >= 0), Status TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, FOREIGN KEY (StudentId) REFERENCES students (Id) ON DELETE RESTRICT, FOREIGN KEY (StoreItemId) REFERENCES store_items (Id) ON DELETE RESTRICT);""",
            """CREATE INDEX IF NOT EXISTS IX_store_orders_StudentId_CreatedAt ON store_orders (StudentId, CreatedAt);"""
            ,"""CREATE TABLE IF NOT EXISTS sync_approvals (Id TEXT NOT NULL PRIMARY KEY, ClientId TEXT NOT NULL, ChangesJson TEXT NOT NULL, Reason TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL, DecidedAt TEXT NULL, CompletedAt TEXT NULL);"""
            ,"""CREATE INDEX IF NOT EXISTS IX_sync_approvals_ClientId_CreatedAt ON sync_approvals (ClientId, CreatedAt);"""
            ,"""CREATE TABLE IF NOT EXISTS synced_file_versions (Id TEXT NOT NULL PRIMARY KEY, ClientId TEXT NOT NULL, RelativePath TEXT NOT NULL, StoragePath TEXT NOT NULL, Sha256 TEXT NOT NULL, Size INTEGER NOT NULL, Label TEXT NOT NULL, CreatedAt TEXT NOT NULL);"""
            ,"""CREATE INDEX IF NOT EXISTS IX_synced_file_versions_ClientId_RelativePath_CreatedAt ON synced_file_versions (ClientId, RelativePath, CreatedAt);"""
            ,"""CREATE TABLE IF NOT EXISTS quiz_sessions (Id TEXT NOT NULL PRIMARY KEY, Question TEXT NOT NULL, OptionsJson TEXT NOT NULL, CorrectIndex INTEGER NOT NULL, XpReward INTEGER NOT NULL, IsActive INTEGER NOT NULL, CreatedAt TEXT NOT NULL);"""
            ,"""CREATE TABLE IF NOT EXISTS quiz_answers (Id TEXT NOT NULL PRIMARY KEY, SessionId TEXT NOT NULL, ClientId TEXT NOT NULL, StudentId TEXT NULL, SelectedIndex INTEGER NOT NULL, IsCorrect INTEGER NOT NULL, XpAwarded INTEGER NOT NULL, AnsweredAt TEXT NOT NULL, FOREIGN KEY (SessionId) REFERENCES quiz_sessions (Id) ON DELETE CASCADE);"""
            ,"""CREATE UNIQUE INDEX IF NOT EXISTS IX_quiz_answers_SessionId_ClientId ON quiz_answers (SessionId, ClientId);"""
            ,"""CREATE TABLE IF NOT EXISTS audit_events (Id TEXT NOT NULL PRIMARY KEY, Category TEXT NOT NULL, Action TEXT NOT NULL, Actor TEXT NOT NULL, Target TEXT NOT NULL, Details TEXT NOT NULL, StatusCode INTEGER NOT NULL, DurationMs INTEGER NOT NULL, CreatedAt TEXT NOT NULL);"""
            ,"""CREATE INDEX IF NOT EXISTS IX_audit_events_CreatedAt ON audit_events (CreatedAt);"""
            ,"""ALTER TABLE students ADD COLUMN Birthday TEXT NULL;"""
        };
        foreach (var statement in statements)
        {
            try { await db.Database.ExecuteSqlRawAsync(statement, cancellationToken); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && statement.Contains("ADD COLUMN", StringComparison.Ordinal))
            {
                // Column already exists on upgraded databases.
            }
        }
    }
}
