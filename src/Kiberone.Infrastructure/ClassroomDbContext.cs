using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class ClassroomDbContext(DbContextOptions<ClassroomDbContext> options) : DbContext(options)
{
    public DbSet<ClassroomGroup> Groups => Set<ClassroomGroup>();
    public DbSet<GroupProgramModule> GroupProgramModules => Set<GroupProgramModule>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<ClassroomSession> ClassroomSessions => Set<ClassroomSession>();
    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<TypingLessonTemplate> TypingLessons => Set<TypingLessonTemplate>();
    public DbSet<TypingLessonStep> TypingLessonSteps => Set<TypingLessonStep>();
    public DbSet<TypingSession> TypingSessions => Set<TypingSession>();
    public DbSet<TypingParticipant> TypingParticipants => Set<TypingParticipant>();
    public DbSet<TypingTelemetrySample> TypingTelemetry => Set<TypingTelemetrySample>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<StudentAchievement> StudentAchievements => Set<StudentAchievement>();
    public DbSet<KiberonTransaction> KiberonTransactions => Set<KiberonTransaction>();
    public DbSet<StoreItem> StoreItems => Set<StoreItem>();
    public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();
    public DbSet<SyncApproval> SyncApprovals => Set<SyncApproval>();
    public DbSet<SyncedFileVersion> SyncedFileVersions => Set<SyncedFileVersion>();
    public DbSet<QuizSession> QuizSessions => Set<QuizSession>();
    public DbSet<QuizAnswer> QuizAnswers => Set<QuizAnswer>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClassroomGroup>(entity =>
        {
            entity.ToTable("groups");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.HasMany(x => x.ProgramModules).WithOne(x => x.Group).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GroupProgramModule>(entity =>
        {
            entity.ToTable("group_program_modules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(240);
            entity.Property(x => x.Id).HasColumnType("TEXT").HasConversion(
                value => value.ToString("D").ToUpperInvariant(),
                value => Guid.Parse(value));
            entity.Property(x => x.GroupId).HasColumnType("TEXT").HasConversion(
                value => value.ToString("D").ToUpperInvariant(),
                value => Guid.Parse(value));
            entity.Property(x => x.StartDate).HasColumnType("TEXT").HasConversion(
                value => value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                value => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            entity.Property(x => x.EndDate).HasColumnType("TEXT").HasConversion(
                value => value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                value => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            entity.HasIndex(x => new { x.GroupId, x.SortOrder });
        });
        modelBuilder.Entity<Student>(entity =>
        {
            entity.ToTable("students", table =>
            {
                table.HasCheckConstraint("CK_students_kiberons", "kiberons >= 0");
                table.HasCheckConstraint("CK_students_xp", "xp >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FirstName).HasMaxLength(120);
            entity.Property(x => x.LastName).HasMaxLength(120);
            entity.Ignore(x => x.DisplayName);
            entity.Ignore(x => x.Level);
            entity.HasOne(x => x.Group).WithMany(x => x.Students).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ClassroomSession>().ToTable("sessions");
        modelBuilder.Entity<Grade>(entity =>
        {
            entity.ToTable("grades", table => table.HasCheckConstraint("CK_grades_value", "value >= 1 AND value <= 5"));
        });
        modelBuilder.Entity<TypingLessonTemplate>(entity =>
        {
            entity.ToTable("typing_lessons");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.KeyboardLayout).HasMaxLength(24);
            entity.Property(x => x.ContentKind).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Lifecycle).HasConversion<string>().HasMaxLength(24);
            entity.HasMany(x => x.Steps).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TypingLessonStep>(entity =>
        {
            entity.ToTable("typing_lesson_steps");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.LessonId, x.Order }).IsUnique();
            entity.Property(x => x.Title).HasMaxLength(160);
            entity.Property(x => x.TargetAccuracy).HasPrecision(5, 2);
        });
        modelBuilder.Entity<TypingSession>(entity =>
        {
            entity.ToTable("typing_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Participants).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TypingParticipant>(entity =>
        {
            entity.ToTable("typing_participants");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.SessionId, x.StudentId }).IsUnique();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Samples).WithOne(x => x.Participant).HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TypingTelemetrySample>(entity =>
        {
            entity.ToTable("typing_telemetry");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.ParticipantId, x.CapturedAt });
        });
        modelBuilder.Entity<Achievement>(entity =>
        {
            entity.ToTable("achievements", table =>
            {
                table.HasCheckConstraint("CK_achievements_xp", "XpReward >= 0");
                table.HasCheckConstraint("CK_achievements_kiberons", "KiberonReward >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(120);
        });
        modelBuilder.Entity<StudentAchievement>(entity =>
        {
            entity.ToTable("student_achievements");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.StudentId, x.AchievementId }).IsUnique();
            entity.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Achievement>().WithMany().HasForeignKey(x => x.AchievementId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KiberonTransaction>(entity =>
        {
            entity.ToTable("kiberon_transactions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.StudentId, x.CreatedAt });
            entity.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StoreItem>(entity =>
        {
            entity.ToTable("store_items", table =>
            {
                table.HasCheckConstraint("CK_store_items_price", "Price >= 0");
                table.HasCheckConstraint("CK_store_items_stock", "Stock >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Sku).IsUnique();
            entity.Property(x => x.Sku).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(120);
        });
        modelBuilder.Entity<StoreOrder>(entity =>
        {
            entity.ToTable("store_orders", table => table.HasCheckConstraint("CK_store_orders_price", "PricePaid >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.StudentId, x.CreatedAt });
            entity.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StoreItem>().WithMany().HasForeignKey(x => x.StoreItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SyncApproval>(entity =>
        {
            entity.ToTable("sync_approvals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.ClientId, x.CreatedAt });
        });
        modelBuilder.Entity<SyncedFileVersion>(entity =>
        {
            entity.ToTable("synced_file_versions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ClientId, x.RelativePath, x.CreatedAt });
        });
        modelBuilder.Entity<QuizSession>(entity =>
        {
            entity.ToTable("quiz_sessions");
            entity.HasKey(x => x.Id);
        });
        modelBuilder.Entity<QuizAnswer>(entity =>
        {
            entity.ToTable("quiz_answers");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.SessionId, x.ClientId }).IsUnique();
            entity.HasOne<QuizSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Category).HasMaxLength(80);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.Property(x => x.Target).HasMaxLength(500);
        });
    }
}
