// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Diagnostics.Entity.Tests.Helpers;
using Microsoft.AspNet.Hosting;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.TestHost;
using Microsoft.AspNet.Testing.xunit;
using Microsoft.Data.Entity;
using Microsoft.Data.Entity.Infrastructure;
using Microsoft.Data.Entity.Migrations;
using Microsoft.Data.Entity.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.AspNet.Diagnostics.Entity.Tests
{
    public class MigrationsEndPointMiddlewareTest
    {
        [Fact]
        public async Task Non_migration_requests_pass_thru()
        {
            var builder = new WebHostBuilder().Configure(app => app
                .UseMigrationsEndPoint()
                .UseMiddleware<SuccessMiddleware>());
            var server = new TestServer(builder);

            HttpResponseMessage response = await server.CreateClient().GetAsync("http://localhost/");

            Assert.Equal("Request Handled", await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        class SuccessMiddleware
        {
            public SuccessMiddleware(RequestDelegate next)
            { }

            public virtual async Task Invoke(HttpContext context)
            {
                await context.Response.WriteAsync("Request Handled");
                context.Response.StatusCode = (int)HttpStatusCode.OK;
            }
        }

        [ConditionalTheory]
        [FrameworkSkipCondition(RuntimeFrameworks.Mono)]
        public async Task Migration_request_default_path()
        {
            await Migration_request(useCustomPath: false);
        }

        [ConditionalTheory]
        [FrameworkSkipCondition(RuntimeFrameworks.Mono)]
        public async Task Migration_request_custom_path()
        {
            await Migration_request(useCustomPath: true);
        }

        private async Task Migration_request(bool useCustomPath)
        {
            using (var database = SqlServerTestStore.CreateScratch())
            {
                var optionsBuilder = new DbContextOptionsBuilder();
                optionsBuilder.UseSqlServer(database.ConnectionString);

                var path = useCustomPath ? new PathString("/EndPoints/ApplyMyMigrations") : MigrationsEndPointOptions.DefaultPath;

                var builder = new WebHostBuilder()
                    .Configure(app =>
                    {
                        if (useCustomPath)
                        {
                            app.UseMigrationsEndPoint(new MigrationsEndPointOptions
                            {
                                Path = path
                            });
                        }
                        else
                        {
                            app.UseMigrationsEndPoint();
                        }
                    })
                    .ConfigureServices(services =>
                    {
                        services.AddEntityFramework().AddSqlServer();
                        services.AddScoped<BloggingContextWithMigrations>();
                        services.AddSingleton(optionsBuilder.Options);
                    });
                var server = new TestServer(builder);

                using (var db = BloggingContextWithMigrations.CreateWithoutExternalServiceProvider(optionsBuilder.Options))
                {
                    var databaseCreator = db.GetService<IRelationalDatabaseCreator>();
                    Assert.False(databaseCreator.Exists());

                    var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("context", typeof(BloggingContextWithMigrations).AssemblyQualifiedName)
                    });

                    HttpResponseMessage response = await server.CreateClient()
                        .PostAsync("http://localhost" + path, formData);

                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

                    Assert.True(databaseCreator.Exists());

                    var historyRepository = db.GetService<IHistoryRepository>();
                    var appliedMigrations = historyRepository.GetAppliedMigrations();
                    Assert.Equal(2, appliedMigrations.Count);
                    Assert.Equal("111111111111111_MigrationOne", appliedMigrations.ElementAt(0).MigrationId);
                    Assert.Equal("222222222222222_MigrationTwo", appliedMigrations.ElementAt(1).MigrationId);
                }
            }
        }

        [Fact]
        public async Task Context_type_not_specified()
        {
            var builder = new WebHostBuilder()
                .Configure(app =>
                {
                    app.UseMigrationsEndPoint();
                });
            var server = new TestServer(builder);

            var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>());

            var response = await server.CreateClient().PostAsync("http://localhost" + MigrationsEndPointOptions.DefaultPath, formData);
            var content = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.StartsWith(StringsHelpers.GetResourceString("FormatMigrationsEndPointMiddleware_NoContextType"), content);
            Assert.True(content.Length > 512);
        }

        [Fact]
        public async Task Invalid_context_type_specified()
        {
            var builder = new WebHostBuilder()
                .Configure(app =>
                {
                    app.UseMigrationsEndPoint();
                });
            var server = new TestServer(builder);

            var typeName = "You won't find this type ;)";
            var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("context", typeName)
                });

            var response = await server.CreateClient().PostAsync("http://localhost" + MigrationsEndPointOptions.DefaultPath, formData);
            var content = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.StartsWith(StringsHelpers.GetResourceString("FormatMigrationsEndPointMiddleware_InvalidContextType", typeName), content);
            Assert.True(content.Length > 512);
        }

        [Fact]
        public async Task Context_not_registered_in_services()
        {
            var builder = new WebHostBuilder()
                .Configure(app => app.UseMigrationsEndPoint())
                .ConfigureServices(services => services.AddEntityFramework().AddSqlServer());
            var server = new TestServer(builder);

            var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("context", typeof(BloggingContext).AssemblyQualifiedName)
                });

            var response = await server.CreateClient().PostAsync("http://localhost" + MigrationsEndPointOptions.DefaultPath, formData);
            var content = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.StartsWith(StringsHelpers.GetResourceString("FormatMigrationsEndPointMiddleware_ContextNotRegistered", typeof(BloggingContext)), content);
            Assert.True(content.Length > 512);
        }

        [ConditionalTheory]
        [FrameworkSkipCondition(RuntimeFrameworks.Mono)]
        public async Task Exception_while_applying_migrations()
        {
            using (var database = SqlServerTestStore.CreateScratch())
            {
                var optionsBuilder = new DbContextOptionsBuilder();
                optionsBuilder.UseSqlServer(database.ConnectionString);

                var builder = new WebHostBuilder()
                    .Configure(app => app.UseMigrationsEndPoint())
                    .ConfigureServices(services =>
                    {
                        services.AddEntityFramework().AddSqlServer();
                        services.AddScoped<BloggingContextWithSnapshotThatThrows>();
                        services.AddSingleton(optionsBuilder.Options);
                    });
                var server = new TestServer(builder);

                var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("context", typeof(BloggingContextWithSnapshotThatThrows).AssemblyQualifiedName)
                    });

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await server.CreateClient().PostAsync("http://localhost" + MigrationsEndPointOptions.DefaultPath, formData));

                Assert.StartsWith(StringsHelpers.GetResourceString("FormatMigrationsEndPointMiddleware_Exception", typeof(BloggingContextWithSnapshotThatThrows)), ex.Message);
                Assert.Equal("Welcome to the invalid migration!", ex.InnerException.Message);
            }
        }
    }
}