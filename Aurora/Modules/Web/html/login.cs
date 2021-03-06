﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Aurora.Framework.Servers.HttpServer;
using Aurora.Framework;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;

namespace Aurora.Modules.Web
{
    public class LoginPage : IWebInterfacePage
    {
        public string FilePath { get { return "html/login.html"; } }

        public bool RequiresAuthentication { get { return false; } }
        public bool RequiresAdminAuthentication { get { return false; } }
        
        public Dictionary<string, object> Fill(WebInterface webInterface, string filename, Hashtable query, OSHttpResponse httpResponse,
            Dictionary<string, object> requestParameters, ITranslator translator)
        {
            var vars = new Dictionary<string, object>();

            string error = "";
            if (requestParameters.ContainsKey("username") && requestParameters.ContainsKey("password"))
            {
                string username = requestParameters["username"].ToString();
                string password = requestParameters["password"].ToString();

                ILoginService loginService = webInterface.Registry.RequestModuleInterface<ILoginService>();
                if (loginService.VerifyClient(UUID.Zero, username, "UserAccount", password, UUID.Zero))
                {
                    UUID sessionID = UUID.Random();
                    UserAccount account = webInterface.Registry.RequestModuleInterface<IUserAccountService>().GetUserAccount(UUID.Zero, username);
                    Authenticator.AddAuthentication(sessionID, account.PrincipalID);
                    if (account.UserLevel > 0)
                        Authenticator.AddAdminAuthentication(sessionID, account.PrincipalID);
                    httpResponse.AddCookie(new System.Web.HttpCookie("SessionID", sessionID.ToString()) { Expires = DateTime.MinValue, Path = "" });

                    httpResponse.StatusCode = (int)HttpStatusCode.Redirect;
                    httpResponse.AddHeader("Location", "/welcomescreen/index.html");

                    return vars;
                }
                else
                    error = "Failed to verify user name and password";
            }

            vars.Add("ErrorMessage", error);
            vars.Add("Login", translator.GetTranslatedString("Login"));
            vars.Add("UserNameText", translator.GetTranslatedString("UserName"));
            vars.Add("PasswordText", translator.GetTranslatedString("Password"));
            vars.Add("ForgotPassword", translator.GetTranslatedString("ForgotPassword"));
            vars.Add("Submit", translator.GetTranslatedString("Submit"));

            return vars;
        }
    }
}
