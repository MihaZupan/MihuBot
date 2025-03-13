﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StorageService.DB;

#nullable disable

namespace StorageService.Migrations
{
    [DbContext(typeof(StorageDbContext))]
    partial class StorageDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.3");

            modelBuilder.Entity("StorageService.Storage.ContainerDbEntry", b =>
                {
                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsPublic")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Owner")
                        .HasColumnType("TEXT");

                    b.Property<long>("RetentionPeriodSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<string>("SasKey")
                        .HasColumnType("TEXT");

                    b.HasKey("Name");

                    b.ToTable("containers");
                });

            modelBuilder.Entity("StorageService.Storage.FileDbEntry", b =>
                {
                    b.Property<string>("Id")
                        .HasColumnType("TEXT");

                    b.Property<string>("ContainerId")
                        .HasColumnType("TEXT");

                    b.Property<long>("ContentLength")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("ExpiresAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Path")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("ContainerId");

                    b.HasIndex("ExpiresAt");

                    b.ToTable("files");
                });

            modelBuilder.Entity("StorageService.Storage.FileDbEntry", b =>
                {
                    b.HasOne("StorageService.Storage.ContainerDbEntry", "Container")
                        .WithMany()
                        .HasForeignKey("ContainerId");

                    b.Navigation("Container");
                });
#pragma warning restore 612, 618
        }
    }
}
