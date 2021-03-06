﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Aurora.Framework.Servers.HttpServer;

namespace Aurora.Modules.Web
{
    public interface IWebInterfacePage
    {
        string FilePath { get; }
        bool RequiresAuthentication { get; }
        bool RequiresAdminAuthentication { get; }

        Dictionary<string, object> Fill(WebInterface webInterface, string filename, Hashtable query, OSHttpResponse httpResponse, Dictionary<string, object> requestParameters, ITranslator translation);
    }
}
