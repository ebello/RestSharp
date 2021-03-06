﻿#region License
//   Copyright 2010 John Sheehan
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License. 
#endregion

#if !SILVERLIGHT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;
using RestSharp.Extensions;

namespace RestSharp
{
	/// <summary>
	/// HttpWebRequest wrapper (sync methods)
	/// </summary>
	public partial class Http
	{
		/// <summary>
		/// Proxy info to be sent with request
		/// </summary>
		public IWebProxy Proxy { get; set; }

		/// <summary>
		/// Execute a POST request
		/// </summary>
		public HttpResponse Post()
		{
			return PostPutInternal("POST");
		}

		/// <summary>
		/// Execute a PUT request
		/// </summary>
		public HttpResponse Put()
		{
			return PostPutInternal("PUT");
		}

		/// <summary>
		/// Execute a GET request
		/// </summary>
		public HttpResponse Get()
		{
			return GetStyleMethodInternal("GET");
		}

		/// <summary>
		/// Execute a HEAD request
		/// </summary>
		public HttpResponse Head()
		{
			return GetStyleMethodInternal("HEAD");
		}

		/// <summary>
		/// Execute an OPTIONS request
		/// </summary>
		public HttpResponse Options()
		{
			return GetStyleMethodInternal("OPTIONS");
		}

		/// <summary>
		/// Execute a DELETE request
		/// </summary>
		public HttpResponse Delete()
		{
			return GetStyleMethodInternal("DELETE");
		}

		// handle restricted headers the .NET way - thanks @dimebrain!
		// http://msdn.microsoft.com/en-us/library/system.net.httpwebrequest.headers.aspx
		private void AppendHeaders(HttpWebRequest webRequest)
		{
			foreach (var header in Headers)
			{
				if (_restrictedHeaderActions.ContainsKey(header.Name))
				{
					_restrictedHeaderActions[header.Name].Invoke(webRequest, header.Value);
				}
				else
				{
					webRequest.Headers.Add(header.Name, header.Value);
				}
			}
		}

		private void AppendCookies(HttpWebRequest webRequest)
		{
			webRequest.CookieContainer = new CookieContainer();
			foreach (var httpCookie in Cookies)
			{
				var cookie = new Cookie
				{
					Name = httpCookie.Name,
					Value = httpCookie.Value,
					Domain = webRequest.RequestUri.Host
				};
				webRequest.CookieContainer.Add(cookie);
			}
		}


		private HttpResponse GetStyleMethodInternal(string method)
		{
			var url = AssembleUrl();
			var webRequest = ConfigureWebRequest(method, url);

			AppendHeaders(webRequest);
			AppendCookies(webRequest);
			return GetResponse(webRequest);
		}

		private HttpResponse PostPutInternal(string method)
		{
			var webRequest = ConfigureWebRequest(method, Url);

			AppendHeaders(webRequest);
			AppendCookies(webRequest);

			PreparePostData(webRequest);

			WriteRequestBody(webRequest);
			return GetResponse(webRequest);
		}

		partial void AddSyncHeaderActions()
		{
			_restrictedHeaderActions.Add("Connection", (r, v) => r.Connection = v);
			_restrictedHeaderActions.Add("Expect", (r, v) => r.Expect = v);
			_restrictedHeaderActions.Add("If-Modified-Since", (r, v) => r.IfModifiedSince = Convert.ToDateTime(v));
			_restrictedHeaderActions.Add("Referer", (r, v) => r.Referer = v);
			_restrictedHeaderActions.Add("Transfer-Encoding", (r, v) => { r.TransferEncoding = v; r.SendChunked = true; });
			_restrictedHeaderActions.Add("User-Agent", (r, v) => r.UserAgent = v);
		}

		private HttpResponse GetResponse(WebRequest request)
		{
			var response = new HttpResponse();
			response.ResponseStatus = ResponseStatus.None;

			try
			{
				var webResponse = GetRawResponse(request);
				ExtractResponseData(response, webResponse);
			}
			catch (Exception ex)
			{
				response.ErrorMessage = ex.Message;
				response.ErrorException = ex;
				response.ResponseStatus = ResponseStatus.Error;
			}

			return response;
		}

		private HttpWebResponse GetRawResponse(WebRequest request)
		{
			HttpWebResponse raw = null;
			try
			{
				raw = (HttpWebResponse)request.GetResponse();
			}
			catch (WebException ex)
			{
				if (ex.Response is HttpWebResponse)
				{
					raw = ex.Response as HttpWebResponse;
				}
			}

			return raw;
		}

		private void PreparePostData(HttpWebRequest webRequest)
		{
			if (HasFiles)
			{
				webRequest.ContentType = GetMultipartFormContentType();
				WriteMultipartFormData(webRequest);
			}
			else
			{
				if (HasParameters)
				{
					webRequest.ContentType = "application/x-www-form-urlencoded";
					RequestBody = EncodeParameters();
				}
				else if (HasBody)
				{
					webRequest.ContentType = RequestContentType;
				}
			}
		}

		private void WriteMultipartFormData(WebRequest webRequest)
		{
			var encoding = Encoding.UTF8;
			using (Stream formDataStream = webRequest.GetRequestStream())
			{
				foreach (var file in Files)
				{
					var fileName = file.FileName;
					var data = file.Data;
					var length = data.Length;
					var contentType = file.ContentType;
					// Add just the first part of this param, since we will write the file data directly to the Stream
					string header = string.Format("--{0}{3}Content-Disposition: form-data; name=\"{1}\"; filename=\"{1}\";{3}Content-Type: {2}{3}{3}",
													FormBoundary,
													fileName,
													contentType ?? "application/octet-stream",
													Environment.NewLine);

					formDataStream.Write(encoding.GetBytes(header), 0, header.Length);
					// Write the file data directly to the Stream, rather than serializing it to a string.
					formDataStream.Write(data, 0, length);
					string lineEnding = Environment.NewLine;
					formDataStream.Write(encoding.GetBytes(lineEnding), 0, lineEnding.Length);
				}

				foreach (var param in Parameters)
				{
					var postData = string.Format("--{0}{3}Content-Disposition: form-data; name=\"{1}\"{3}{3}{2}{3}",
													FormBoundary,
													param.Name,
													param.Value,
													Environment.NewLine);

					formDataStream.Write(encoding.GetBytes(postData), 0, postData.Length);
				}

				string footer = String.Format("{1}--{0}--{1}", FormBoundary, Environment.NewLine);
				formDataStream.Write(encoding.GetBytes(footer), 0, footer.Length);
			}
		}

		private void WriteRequestBody(WebRequest webRequest)
		{
			if (HasBody)
			{
				webRequest.ContentLength = RequestBody.Length;

				var requestStream = webRequest.GetRequestStream();
				using (var writer = new StreamWriter(requestStream, Encoding.UTF8))
				{
					writer.Write(RequestBody);
				}
			}
		}

		private HttpWebRequest ConfigureWebRequest(string method, Uri url)
		{
			var webRequest = (HttpWebRequest)WebRequest.Create(url);
			webRequest.Method = method;

			webRequest.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip | DecompressionMethods.None;

			if (UserAgent.HasValue())
			{
				webRequest.UserAgent = UserAgent;
			}

			if (Timeout != 0)
			{
				webRequest.Timeout = Timeout;
			}

			if (Credentials != null)
			{
				webRequest.Credentials = Credentials;
			}

			if (Proxy != null)
			{
				webRequest.Proxy = Proxy;
			}
			return webRequest;
		}
	}
}
#endif