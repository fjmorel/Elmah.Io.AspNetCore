﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using Elmah.Io.AspNetCore.Breadcrumbs;
using Elmah.Io.AspNetCore.Extensions;
using Elmah.Io.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Elmah.Io.AspNetCore
{
    internal class MessageShipper
    {
        internal static string _assemblyVersion = typeof(MessageShipper).Assembly.GetName().Version.ToString();
        internal static string _aspNetCoreAssemblyVersion = typeof(HttpContext).Assembly.GetName().Version.ToString();

        public static void Ship(Exception exception, string title, HttpContext context, ElmahIoOptions options, IBackgroundTaskQueue queue)
        {
            var baseException = exception?.GetBaseException();
            var utcNow = DateTime.UtcNow;
            var createMessage = new CreateMessage
            {
                DateTime = utcNow,
                Detail = Detail(exception, options),
                Type = baseException?.GetType().FullName,
                Title = title,
                Data = exception.ToDataList(),
                Cookies = Cookies(context),
                Form = Form(context),
                Hostname = Hostname(context),
                ServerVariables = ServerVariables(context),
                StatusCode = StatusCode(exception, context),
                Url = Url(context),
                QueryString = QueryString(context),
                Method = context.Request?.Method,
                Severity = Severity(exception, context),
                Source = Source(baseException),
                Application = options.Application,
                Breadcrumbs = Breadcrumbs(context, utcNow)
            };

            TrySetUser(context, createMessage);

            if (options.OnFilter != null && options.OnFilter(createMessage))
            {
                return;
            }

            queue.QueueBackgroundWorkItem(async token =>
            {
                var elmahioApi = ElmahioAPI.Create(options.ApiKey, new Client.ElmahIoOptions
                {
                    WebProxy = options.WebProxy,
                    Timeout = new TimeSpan(0, 0, 30), // Storing the message is behind a queue why the default timeout of 5 seconds isn't needed here.
                    UserAgent = UserAgent(),
                });

                elmahioApi.Messages.OnMessage += (sender, args) =>
                {
                    options.OnMessage?.Invoke(args.Message);
                };
                elmahioApi.Messages.OnMessageFail += (sender, args) =>
                {
                    options.OnError?.Invoke(args.Message, args.Error);
                };

                try
                {
                    await elmahioApi.Messages.CreateAndNotifyAsync(options.LogId, createMessage);
                }
                catch (Exception e)
                {
                    options.OnError?.Invoke(createMessage, e);
                    // If there's a Exception while generating the error page, re-throw the original exception.
                }
            });
        }

        private static string UserAgent()
        {
            return new StringBuilder()
                .Append(new ProductInfoHeaderValue(new ProductHeaderValue("Elmah.Io.AspNetCore", _assemblyVersion)).ToString())
                .Append(" ")
                .Append(new ProductInfoHeaderValue(new ProductHeaderValue("Microsoft.AspNetCore.Http", _aspNetCoreAssemblyVersion)).ToString())
                .ToString();
        }

        private static IList<Breadcrumb> Breadcrumbs(HttpContext context, DateTime utcNow)
        {
            var feature = context?.Features?.Get<ElmahIoBreadcrumbFeature>();
            if (feature?.Breadcrumbs?.Count > 0)
            {
                // Set default values on properties not set
                foreach (var breadcrumb in feature.Breadcrumbs)
                {
                    if (!breadcrumb.DateTime.HasValue) breadcrumb.DateTime = utcNow;
                    if (string.IsNullOrWhiteSpace(breadcrumb.Severity)) breadcrumb.Severity = "Information";
                    if (string.IsNullOrWhiteSpace(breadcrumb.Action)) breadcrumb.Action = "Log";
                }

                var breadcrumbs = feature.Breadcrumbs.OrderByDescending(l => l.DateTime).ToList();
                feature.Breadcrumbs.Clear();
                return breadcrumbs;
            }

            return null;
        }

        private static string Url(HttpContext context)
        {
            if (context.Request == null) return null;
            if (context.Request.Path.HasValue) return context.Request.Path.Value;
            if (context.Request.PathBase.HasValue) return context.Request.PathBase.Value;

            return null;
        }

        private static string Hostname(HttpContext context)
        {
            var machineName = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machineName)) return machineName;

            machineName = Environment.GetEnvironmentVariable("COMPUTERNAME");
            if (!string.IsNullOrWhiteSpace(machineName)) return machineName;

            return context.Request?.Host.Host;
        }

        private static string Source(Exception baseException)
        {
            return baseException?.Source;
        }

        private static void TrySetUser(HttpContext context, CreateMessage createMessage)
        {
            try
            {
                createMessage.User = context?.User?.Identity?.Name;
            }
            catch
            {
                // ASP.NET Core < 2.0 is broken. When creating a new ASP.NET Core 1.x project targeting .NET Framework
                // .NET throws a runtime error complaining about missing System.Security.Claims. For this reason,
                // we don't support setting the User property for 1.x projects targeting .NET Framework.
                // Check out the following GitHub issues for details:
                // - https://github.com/dotnet/standard/issues/410
                // - https://github.com/dotnet/sdk/issues/901
            }
        }

        private static string Severity(Exception exception, HttpContext context)
        {
            var statusCode = StatusCode(exception, context);

            if (statusCode.HasValue && statusCode >= 400 && statusCode < 500) return Client.Severity.Warning.ToString();
            if (statusCode.HasValue && statusCode >= 500) return Client.Severity.Error.ToString();
            if (exception != null) return Client.Severity.Error.ToString();

            return null; // Let elmah.io decide when receiving the message
        }

        private static int? StatusCode(Exception exception, HttpContext context)
        {
            if (exception != null)
            {
                // If an exception is thrown, but the response has a successful status code,
                // it is because the elmah.io middleware are running before the correct
                // status code is assigned the response. Override it with 500.
                return context.Response?.StatusCode < 400 ? 500 : context.Response?.StatusCode;
            }

            return context.Response?.StatusCode;
        }

        private static string Detail(Exception exception, ElmahIoOptions options)
        {
            if (exception == null) return null;
            return options.ExceptionFormatter != null
                ? options.ExceptionFormatter.Format(exception)
                : exception.ToString();
        }

        private static List<Item> Cookies(HttpContext httpContext)
        {
            return httpContext.Request?.Cookies?.Keys.Select(k => new Item(k, httpContext.Request.Cookies[k])).ToList();
        }

        private static List<Item> Form(HttpContext httpContext)
        {
            try
            {
                return httpContext.Request?.Form?.Keys.Select(k => new Item(k, httpContext.Request.Form[k])).ToList();
            }
            catch (Exception)
            {
                // All sorts of exceptions can happen while trying to read from Request.Form. Like:
                // - InvalidOperationException: Request not a form POST or similar
                // - InvalidDataException: Form body without a content-type or similar
                // - ConnectionResetException: More than 100 active connections or similar
            }

            return null;
        }

        private static List<Item> ServerVariables(HttpContext httpContext)
        {
            var serverVariables = new List<Item>();
            serverVariables.AddRange(RequestHeaders(httpContext.Request));
            serverVariables.AddRange(Features(httpContext.Features));
            return serverVariables;
        }

        private static IEnumerable<Item> RequestHeaders(HttpRequest request)
        {
            return request?.Headers?.Keys.Select(k => new Item(k, request.Headers[k])).ToList();
        }

        private static IEnumerable<Item> Features(IFeatureCollection features)
        {
            var items = new List<Item>();
            if (features == null) return items;

            foreach (var property in features.GetType().GetProperties())
            {
                try
                {
                    var value = property.GetValue(features);
                    if (value.IsValidForItems()) items.Add(new Item(property.Name, value.ToString()));
                }
                catch {}
            }

            return items;
        }

        private static List<Item> QueryString(HttpContext httpContext)
        {
            return httpContext.Request?.Query?.Keys.Select(k => new Item(k, httpContext.Request.Query[k])).ToList();
        }
    }
}