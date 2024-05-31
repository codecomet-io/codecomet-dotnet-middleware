using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Net.Http;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodeComet
{
    public class CaptureMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly HttpClient _httpClient;
        private readonly string _projectID;
        private readonly string _externalServerUrl;

        private readonly bool _captureAll;
        private const string DefaultExternalServerUrl = "http://app.codecomet.io/api/trafficconsumer.TrafficService/IngestTrafficLog";


        private const string RFC3339Fmt = "yyyy-MM-ddTHH:mm:ss.fffK";

        private string appDirectory = AppContext.BaseDirectory;

        public CaptureMiddleware(RequestDelegate next, string apiKey, string projectID, bool captureAll = false,
            string externalServerUrl = null)
        {
            _next = next;
            _projectID = projectID;
            _externalServerUrl = externalServerUrl ?? DefaultExternalServerUrl;
            _captureAll = captureAll;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Api-Key", apiKey);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var staticExtensions = new HashSet<string> { ".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".woff2" };
            if (staticExtensions.Any(ext => context.Request.Path.Value.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                // It's a static file request, so just continue to the next middleware
                await _next(context);
            }
            else
            {
                var requestData = new Dictionary<string, object>();
                var originalBodyStream = context.Response.Body;
                // Capture the request timestamp
                var requestTimestamp = DateTimeOffset.UtcNow;
                requestData["request_time"] = requestTimestamp.ToString(RFC3339Fmt);
                requestData["project_id"] = _projectID;
                using (var responseBody = new MemoryStream())
                {
                    context.Response.Body = responseBody;

                    try
                    {
                        // Collect request and response details
                        await CollectRequestData(context, requestData);
                        await _next(context);

                        context.Response.Body.Seek(0, SeekOrigin.Begin);
                        var responseBodyText = await new StreamReader(context.Response.Body).ReadToEndAsync();
                        context.Response.Body.Seek(0, SeekOrigin.Begin);

                        await responseBody.CopyToAsync(originalBodyStream);
                        // collect response data
                        // Capture the response timestamp
                        var responseTimestamp = DateTimeOffset.UtcNow;
                        requestData["response_time"] = responseTimestamp.ToString(RFC3339Fmt);

                        requestData["raw_response"] = responseBodyText;
                        requestData["status_code"] = context.Response.StatusCode;
                    }
                    catch (Exception ex)
                    {
                        if (context.Response.StatusCode == 200 || context.Response.StatusCode == 0)
                        {
                            context.Response.StatusCode = 500;
                        }
                        requestData["status_code"] = context.Response.StatusCode;
                        HandleException(context, ex, requestData);
                        throw;
                    }
                    finally
                    {
                        context.Response.Body = originalBodyStream;
                        // Send data to external server
                        await SendDataToExternalServer(requestData);
                    }
                }
            }
        }

        private string GetRawHeaders(IHeaderDictionary headers)
        {
            var stringBuilder = new StringBuilder();

            foreach (var header in headers)
            {
                foreach (var value in header.Value)
                {
                    stringBuilder.AppendLine($"{header.Key}: {value}");
                }
            }

            return stringBuilder.ToString();
        }

        private async Task CollectRequestData(HttpContext context, Dictionary<string, object> data)
        {
            var request = context.Request;

            // Ensure the request body can be read multiple times
            request.EnableBuffering();

            // Read the request body
            var body = string.Empty;
            var requestBodyStream = new MemoryStream();
            await request.Body.CopyToAsync(requestBodyStream);
            requestBodyStream.Seek(0, SeekOrigin.Begin);

            using (var reader = new StreamReader(requestBodyStream))
            {
                body = await reader.ReadToEndAsync();
                // Reset the stream so that it can be read again later
                request.Body.Seek(0, SeekOrigin.Begin);
            }


            data["method"] = request.Method;
            data["path"] = GetRequestPath(request);
            data["query_string"] = request.QueryString.ToString();
            data["request_headers"] = GetRawHeaders(request.Headers);
            data["raw_request"] = body;
        }

        private string GetRequestPath(HttpRequest request)
        {
            return request.Path.HasValue ? request.Path.ToString() : string.Empty;
        }

        private void HandleException(HttpContext context, Exception exception, Dictionary<string, object> data)
        {
            data["exception_message"] = exception.Message;
            data["traceback"] = exception.StackTrace;
        }

        private async Task SendDataToExternalServer(Dictionary<string, object> data)
        {
            if (!_captureAll && data.ContainsKey("status_code") && data["status_code"] is int statusCode && statusCode < 500)
            {
                return;
            }
            try
            {
                data["executable_path"] = appDirectory;
                var wrapper = new Dictionary<string, object>();
                wrapper["log"] = data;
                var content = new StringContent(JsonSerializer.Serialize(wrapper), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(_externalServerUrl, content);

                // Check the response
                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Failed to log data: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending data to external server: {ex.Message}");
            }

        }
    }
}