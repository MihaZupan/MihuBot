﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MihuBot.DB.GitHub;

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    [DbContext(typeof(GitHubDbContext))]
    partial class GitHubDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.6");

            modelBuilder.Entity("IssueInfoLabelInfo", b =>
                {
                    b.Property<string>("IssuesId")
                        .HasColumnType("TEXT");

                    b.Property<string>("LabelsId")
                        .HasColumnType("TEXT");

                    b.HasKey("IssuesId", "LabelsId");

                    b.HasIndex("LabelsId");

                    b.ToTable("IssueInfoLabelInfo");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.BodyEditHistoryEntry", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsComment")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PreviousBody")
                        .HasColumnType("TEXT");

                    b.Property<string>("ResourceIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("body_edit_history");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.CommentInfo", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("TEXT");

                    b.Property<int>("AuthorAssociation")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Body")
                        .HasColumnType("TEXT");

                    b.Property<int>("Confused")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("Eyes")
                        .HasColumnType("INTEGER");

                    b.Property<long>("GitHubIdentifier")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Heart")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Hooray")
                        .HasColumnType("INTEGER");

                    b.Property<string>("HtmlUrl")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsPrReviewComment")
                        .HasColumnType("INTEGER");

                    b.Property<string>("IssueId")
                        .HasColumnType("TEXT");

                    b.Property<int>("Laugh")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Minus1")
                        .HasColumnType("INTEGER");

                    b.Property<string>("NodeIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<int>("Plus1")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Rocket")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<long>("UserId")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("IssueId");

                    b.HasIndex("UpdatedAt");

                    b.HasIndex("UserId");

                    b.ToTable("comments");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.IngestedEmbeddingRecord", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<string>("ResourceIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("ResourceIdentifier");

                    b.ToTable("ingested_embedding_history");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.IngestedFullTextSearchRecord", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<string>("ResourceIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("ResourceIdentifier");

                    b.ToTable("ingested_fts_record_history");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.IssueInfo", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("TEXT");

                    b.Property<int?>("ActiveLockReason")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Body")
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("ClosedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("Confused")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("Eyes")
                        .HasColumnType("INTEGER");

                    b.Property<long>("GitHubIdentifier")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Heart")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Hooray")
                        .HasColumnType("INTEGER");

                    b.Property<string>("HtmlUrl")
                        .HasColumnType("TEXT");

                    b.Property<int>("Laugh")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("Locked")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Minus1")
                        .HasColumnType("INTEGER");

                    b.Property<string>("NodeIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<int>("Number")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Plus1")
                        .HasColumnType("INTEGER");

                    b.Property<long>("RepositoryId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Rocket")
                        .HasColumnType("INTEGER");

                    b.Property<int>("State")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Title")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<long>("UserId")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("CreatedAt");

                    b.HasIndex("Number");

                    b.HasIndex("RepositoryId");

                    b.HasIndex("UpdatedAt");

                    b.HasIndex("UserId");

                    b.ToTable("issues");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.LabelInfo", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("TEXT");

                    b.Property<string>("Color")
                        .HasColumnType("TEXT");

                    b.Property<string>("Description")
                        .HasColumnType("TEXT");

                    b.Property<long>("GitHubIdentifier")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<string>("NodeIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<long>("RepositoryId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Url")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("RepositoryId");

                    b.ToTable("labels");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.PullRequestInfo", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("TEXT");

                    b.Property<int>("Additions")
                        .HasColumnType("INTEGER");

                    b.Property<int>("ChangedFiles")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Commits")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Deletions")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("Draft")
                        .HasColumnType("INTEGER");

                    b.Property<long>("GitHubIdentifier")
                        .HasColumnType("INTEGER");

                    b.Property<string>("IssueId")
                        .HasColumnType("TEXT");

                    b.Property<bool?>("MaintainerCanModify")
                        .HasColumnType("INTEGER");

                    b.Property<string>("MergeCommitSha")
                        .HasColumnType("TEXT");

                    b.Property<bool?>("Mergeable")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("MergeableState")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime?>("MergedAt")
                        .HasColumnType("TEXT");

                    b.Property<long?>("MergedById")
                        .HasColumnType("INTEGER");

                    b.Property<string>("NodeIdentifier")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("IssueId")
                        .IsUnique();

                    b.ToTable("pullrequests");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.RepositoryInfo", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<bool>("Archived")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Description")
                        .HasColumnType("TEXT");

                    b.Property<string>("FullName")
                        .HasColumnType("TEXT");

                    b.Property<string>("HtmlUrl")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastIssueCommentsUpdate")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastIssuesUpdate")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastPullRequestReviewCommentsUpdate")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastRepositoryMetadataUpdate")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<string>("NodeIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<long>("OwnerId")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("Private")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("OwnerId");

                    b.ToTable("repositories");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.TriagedIssueRecord", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Body")
                        .HasColumnType("TEXT");

                    b.Property<string>("IssueId")
                        .HasColumnType("TEXT");

                    b.Property<int>("TriageReportIssueNumber")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("IssueId");

                    b.ToTable("triaged_issues");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.UserInfo", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Bio")
                        .HasColumnType("TEXT");

                    b.Property<string>("Company")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("EntryUpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("Followers")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Following")
                        .HasColumnType("INTEGER");

                    b.Property<string>("HtmlUrl")
                        .HasColumnType("TEXT");

                    b.Property<string>("Location")
                        .HasColumnType("TEXT");

                    b.Property<string>("Login")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<string>("NodeIdentifier")
                        .HasColumnType("TEXT");

                    b.Property<int?>("Type")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.ToTable("users");
                });

            modelBuilder.Entity("IssueInfoLabelInfo", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.IssueInfo", null)
                        .WithMany()
                        .HasForeignKey("IssuesId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("MihuBot.DB.GitHub.LabelInfo", null)
                        .WithMany()
                        .HasForeignKey("LabelsId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.CommentInfo", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.IssueInfo", "Issue")
                        .WithMany("Comments")
                        .HasForeignKey("IssueId");

                    b.HasOne("MihuBot.DB.GitHub.UserInfo", "User")
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Issue");

                    b.Navigation("User");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.IssueInfo", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.RepositoryInfo", "Repository")
                        .WithMany("Issues")
                        .HasForeignKey("RepositoryId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("MihuBot.DB.GitHub.UserInfo", "User")
                        .WithMany("Issues")
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Repository");

                    b.Navigation("User");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.LabelInfo", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.RepositoryInfo", "Repository")
                        .WithMany("Labels")
                        .HasForeignKey("RepositoryId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Repository");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.PullRequestInfo", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.IssueInfo", "Issue")
                        .WithOne("PullRequest")
                        .HasForeignKey("MihuBot.DB.GitHub.PullRequestInfo", "IssueId");

                    b.Navigation("Issue");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.RepositoryInfo", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.UserInfo", "Owner")
                        .WithMany()
                        .HasForeignKey("OwnerId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Owner");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.TriagedIssueRecord", b =>
                {
                    b.HasOne("MihuBot.DB.GitHub.IssueInfo", "Issue")
                        .WithMany()
                        .HasForeignKey("IssueId");

                    b.Navigation("Issue");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.IssueInfo", b =>
                {
                    b.Navigation("Comments");

                    b.Navigation("PullRequest");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.RepositoryInfo", b =>
                {
                    b.Navigation("Issues");

                    b.Navigation("Labels");
                });

            modelBuilder.Entity("MihuBot.DB.GitHub.UserInfo", b =>
                {
                    b.Navigation("Issues");
                });
#pragma warning restore 612, 618
        }
    }
}
