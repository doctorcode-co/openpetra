﻿//
// DO NOT REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
//
// @Authors:
//       timop, christiank
//
// Copyright 2004-2020 by OM International
//
// This file is part of OpenPetra.org.
//
// OpenPetra.org is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// OpenPetra.org is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with OpenPetra.org.  If not, see <http://www.gnu.org/licenses/>.
//
using System;
using System.IO;
using System.Collections.Generic;
using System.Web;
using System.Threading;
using System.Web.SessionState;
using System.Data;
using System.Data.Odbc;
using System.Linq;

using Mono.Data.Sqlite;

using Ict.Common;
using Ict.Common.DB;

using Newtonsoft.Json;

namespace Ict.Common.Session
{
    /// <summary>
    /// Static class for storing sessions.
    /// we are using our own session handling,
    /// since we want to store sessions in the database,
    /// and we want to run tests without HttpContext.
    /// </summary>
    public class TSession
    {
        private const int SessionValidHours = 24;

        private static Mutex FDeleteSessionMutex = new Mutex(); // STATIC_OK: Mutex

        // these variables are only used per thread, they are initialized for each request.
        [ThreadStatic]
        private static string FSessionID;
        [ThreadStatic]
        private static SortedList <string, string> FSessionValues;

        /// <summary>
        /// Set the session id for this current thread.
        /// Each request has its own thread.
        /// Threads can be reused for different users.
        /// </summary>
        public static void InitThread(string AThreadDescription, string AConfigFileName, string ASessionID = null)
        {
            TLogWriter.ResetStaticVariables();
            TLogging.ResetStaticVariables();

            new TAppSettingsManager(AConfigFileName);
            new TLogging(TSrvSetting.ServerLogFile);
            TLogging.DebugLevel = TAppSettingsManager.GetInt16("Server.DebugLevel", 0);

            string httprequest = "";
            if ((HttpContext.Current != null) && (HttpContext.Current.Request != null))
            {
                httprequest = " for path " + HttpContext.Current.Request.PathInfo;
            }
            
            TLogging.LogAtLevel(4, AThreadDescription + ": Running InitThread for thread id " + Thread.CurrentThread.ManagedThreadId.ToString() + httprequest);

            FSessionID = ASessionID;
            FSessionValues = null;

            string sessionID;

            if (ASessionID == null)
            {
                sessionID = FindSessionID();
            }
            else
            {
                sessionID = ASessionID;
            }

            // avoid dead lock on parallel logins
            FDeleteSessionMutex.WaitOne();

            TDataBase db = ConnectDB("SessionInitThread");

            TDBTransaction t = new TDBTransaction();
            bool SubmissionOK = false;
            bool newSession = false;

            db.WriteTransaction(ref t,
                ref SubmissionOK,
                delegate
                {
                    // get the session ID, or start a new session
                    // load the session values from the database
                    // update the session last access in the database
                    // clean old sessions
                    newSession = InitSession(sessionID, t);

                    SubmissionOK = true;
                });

            if (newSession)
            {
                // use a separate transaction to clean old sessions
                db.WriteTransaction(ref t,
                    ref SubmissionOK,
                    delegate
                    {
                        CleanOldSessions(t);
                        SubmissionOK = true;
                    });
            }

            db.CloseDBConnection();

            FDeleteSessionMutex.ReleaseMutex();
        }

        /// get the current session id from the http context
        private static string FindSessionID()
        {
            if ((HttpContext.Current != null) && (HttpContext.Current.Request.Cookies.AllKeys.Contains("OpenPetraSessionID")))
            {
                string sessionId = HttpContext.Current.Request.Cookies["OpenPetraSessionID"].Value;
                if (sessionId != String.Empty)
                {
                    TLogging.LogAtLevel(4, "FindSessionID: Session ID found in HttpContext. SessionID: " + sessionId);
                    return sessionId;
                }
            }

            TLogging.LogAtLevel(1, "FindSessionID: Session ID not found in the HttpContext");
            return string.Empty;
        }

        /// <summary>
        /// gets the current session id, or creates a new session id if it does not exist yet.
        /// loads the session values or initializes them.
        /// clean old sessions from the database.
        /// </summary>
        /// <returns>true if new session was started</returns>
        private static bool InitSession(string ASessionID, TDBTransaction AWriteTransaction)
        {
            string sessionID = ASessionID;
            bool newSession = false;

            // is that session still valid?
            if ((sessionID != string.Empty) && !HasValidSession(sessionID, AWriteTransaction))
            {
                TLogging.LogAtLevel(1,"TSession: session ID is not valid anymore: " + sessionID);

                sessionID = string.Empty;

                if (HttpContext.Current != null)
                {
                    HttpContext.Current.Request.Cookies.Remove("OpenPetraSessionID");
                }
            }

            // we need a new session
            if (sessionID == string.Empty)
            {
                sessionID = Guid.NewGuid().ToString();
                TLogging.LogAtLevel(1, "TSession: Creating new session: " + sessionID + " in Thread " + Thread.CurrentThread.ManagedThreadId.ToString());

                if (HttpContext.Current != null)
                {
                    HttpContext.Current.Request.Cookies.Add(new HttpCookie("OpenPetraSessionID", sessionID));
                    HttpContext.Current.Response.Cookies.Add(new HttpCookie("OpenPetraSessionID", sessionID));
                }

                // store new session
                FSessionID = sessionID;
                FSessionValues = new SortedList <string, string>();
                SaveSession(AWriteTransaction);

                newSession = true;
            }
            else
            {
                TLogging.LogAtLevel(1, "TSession: Loading valid session from database: " + sessionID + " in Thread " + Thread.CurrentThread.ManagedThreadId.ToString());
                FSessionID = sessionID;
                LoadSession(AWriteTransaction);
                UpdateLastAccessTime(AWriteTransaction);
            }

            return newSession;
        }

        private static bool HasValidSession(string ASessionID, TDBTransaction AReadTransaction)
        {
            string sql = "SELECT COUNT(*) FROM PUB_s_session WHERE s_session_id_c = ? and s_valid_until_d > NOW()";
            OdbcParameter[] parameters = new OdbcParameter[1];
            parameters[0] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
            parameters[0].Value = ASessionID;
            
            if (Convert.ToInt32(AReadTransaction.DataBaseObj.ExecuteScalar(sql, AReadTransaction, parameters)) == 1)
            {
                return true;
            }

            return false;
        }

        /// establish a database connection to the alternative sqlite database for the sessions
        private static TDataBase EstablishDBConnectionSqliteSessionDB(String AConnectionName = "")
        {
            TDBType DBType = CommonTypes.ParseDBType(TAppSettingsManager.GetValue("Server.RDBMSType", "postgresql"));

            if (DBType != TDBType.SQLite)
            {
                throw new Exception("EstablishDBConnectionSqliteSessionDB: we should not get here.");
            }

            string DatabaseHostOrFile = TAppSettingsManager.GetValue("Server.DBSqliteSession", "localhost");
            string DatabasePort = String.Empty;
            string DatabaseName = TAppSettingsManager.GetValue("Server.DBName", "openpetra");
            string DBUsername = TAppSettingsManager.GetValue("Server.DBUserName", "petraserver");
            string DBPassword = TAppSettingsManager.GetValue("Server.DBPassword", string.Empty, false);

            if (!File.Exists(DatabaseHostOrFile))
            {
                // create the sessions database file
                TLogging.Log("create the sessions database file: " + DatabaseHostOrFile);

                // sqlite on Windows does not support encryption with a password
                // System.EntryPointNotFoundException: sqlite3_key
                DBPassword = string.Empty;

                SqliteConnection conn = new SqliteConnection("Data Source=" + DatabaseHostOrFile + (DBPassword.Length > 0 ? ";Password=" + DBPassword : ""));
                conn.Open();

                string createStmt = 
                    @"CREATE TABLE s_session (
                      s_session_id_c varchar(128) NOT NULL,
                      s_valid_until_d datetime NOT NULL,
                      s_session_values_c text,
                      s_date_created_d date,
                      s_created_by_c varchar(20),
                      s_date_modified_d date,
                      s_modified_by_c varchar(20),
                      s_modification_id_t timestamp,
                      CONSTRAINT s_session_pk
                        PRIMARY KEY (s_session_id_c)
                    )";

                SqliteCommand cmd = new SqliteCommand(createStmt, conn);
                cmd.ExecuteNonQuery();
                conn.Close();
            }

            TDataBase DBAccessObj = new TDataBase();

            DBAccessObj.EstablishDBConnection(DBType,
                DatabaseHostOrFile,
                DatabasePort,
                DatabaseName,
                DBUsername,
                DBPassword,
                "",
                true,
                AConnectionName);

            return DBAccessObj;
        }

        private static TDataBase ConnectDB(string AConnectionName)
        {
            // for SQLite, we use a different database for the session data, to avoid locking the database.
            if (DBAccess.DBType == TDBType.SQLite)
            {
                return EstablishDBConnectionSqliteSessionDB(AConnectionName);
            }

            return DBAccess.Connect(AConnectionName);
        }

        private static void UpdateLastAccessTime(TDBTransaction AWriteTransaction)
        {
            OdbcParameter[] parameters = new OdbcParameter[2];
            parameters[0] = new OdbcParameter("s_modification_id_t", OdbcType.DateTime);
            parameters[0].Value = DateTime.Now;
            parameters[1] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
            parameters[1].Value = FSessionID;
            string sql = "UPDATE PUB_s_session SET s_modification_id_t = ? WHERE s_session_id_c = ?";
            AWriteTransaction.DataBaseObj.ExecuteNonQuery(sql, AWriteTransaction, parameters);
        }

        private static void LoadSession(TDBTransaction AReadTransaction)
        {
            OdbcParameter[] parameters = new OdbcParameter[1];
            parameters[0] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
            parameters[0].Value = FSessionID;

            string sql = "SELECT s_session_values_c FROM s_session WHERE s_session_id_c = ?";
            string jsonString = AReadTransaction.DataBaseObj.ExecuteScalar(sql, AReadTransaction, parameters).ToString();
            FSessionValues = JsonConvert.DeserializeObject<SortedList <string, string>>(jsonString);
        }

        private static void SaveSession(TDBTransaction AWriteTransaction)
        {
            string sql = "SELECT COUNT(*) FROM PUB_s_session WHERE s_session_id_c = ?";
            OdbcParameter[] parameters = new OdbcParameter[1];
            parameters[0] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
            parameters[0].Value = FSessionID;
            
            if (Convert.ToInt32(AWriteTransaction.DataBaseObj.ExecuteScalar(sql, AWriteTransaction, parameters)) == 1)
            {
                parameters = new OdbcParameter[2];
                parameters[0] = new OdbcParameter("s_session_values_c", OdbcType.Text);
                parameters[0].Value = JsonConvert.SerializeObject(FSessionValues);
                parameters[1] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
                parameters[1].Value = FSessionID;
                sql = "UPDATE PUB_s_session SET s_session_values_c = ? WHERE s_session_id_c = ?";
            }
            else
            {
                parameters = new OdbcParameter[3];
                parameters[0] = new OdbcParameter("s_session_values_c", OdbcType.Text);
                parameters[0].Value = JsonConvert.SerializeObject(FSessionValues);
                parameters[1] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
                parameters[1].Value = FSessionID;
                parameters[2] = new OdbcParameter("s_valid_until_d", OdbcType.DateTime);
                parameters[2].Value = DateTime.Now.AddHours(SessionValidHours);
                sql = "INSERT INTO PUB_s_session (s_session_values_c, s_session_id_c, s_valid_until_d) VALUES (?,?,?)";
            }

            AWriteTransaction.DataBaseObj.ExecuteNonQuery(sql, AWriteTransaction, parameters);
        }

        /// clean all old sessions
        static private void CleanOldSessions(TDBTransaction AWriteTransaction)
        {
            string sql = "SELECT COUNT(*) FROM PUB_s_session WHERE s_valid_until_d < NOW()";
            if (Convert.ToInt32(AWriteTransaction.DataBaseObj.ExecuteScalar(sql, AWriteTransaction)) > 0)
            {
                sql = "DELETE FROM PUB_s_session WHERE s_valid_until_d < NOW()";
                AWriteTransaction.DataBaseObj.ExecuteNonQuery(sql, AWriteTransaction);
            }
        }

        /// <summary>
        /// set a session variable.
        /// store to database immediately
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public static void SetVariable(string name, object value)
        {
            TDataBase db = ConnectDB("SessionSetVariable");

            TDBTransaction t = new TDBTransaction();
            bool SubmissionOK = false;

            db.WriteTransaction(ref t, ref SubmissionOK,
                delegate
                {
                    if (FSessionValues.Keys.Contains(name))
                    {
                        FSessionValues[name] = (new TVariant(value)).EncodeToString();
                    }
                    else
                    {
                        FSessionValues.Add(name, (new TVariant(value)).EncodeToString());
                    }

                    SaveSession(t);

                    SubmissionOK = true;
                });

            db.CloseDBConnection();
        }

        /// <summary>
        /// returns true if variable exists and is not null
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static bool HasVariable(string name)
        {
            bool result = false;

            if ((FSessionValues != null) && FSessionValues.Keys.Contains(name) && (FSessionValues[name] != null))
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// get a session variable
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static object GetVariable(string name)
        {
            if ((FSessionValues != null) && FSessionValues.Keys.Contains(name))
            {
                return TVariant.DecodeFromString(FSessionValues[name]).ToObject();
            }

            return null;
        }

        /// <summary>
        /// get a session variable, not decoded yet
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static TVariant GetVariant(string name)
        {
            if ((FSessionValues != null) && FSessionValues.Keys.Contains(name))
            {
                return TVariant.DecodeFromString(FSessionValues[name]);
            }

            return new TVariant((object)null);
        }

        /// get the session values from the database again
        public static void RefreshFromDatabase()
        {
            TDataBase db = ConnectDB("SessionRefresh");
            TDBTransaction t = new TDBTransaction();

            db.ReadTransaction(ref t,
                delegate
                {
                    LoadSession(t);
                });

            db.CloseDBConnection();
        }

        /// get the session id, to pass to a sub thread
        public static string GetSessionID()
        {
            return FSessionID;
        }

        private static void RemoveSession()
        {
            TDataBase db = ConnectDB("RemoveSession");
            TDBTransaction t = new TDBTransaction();
            bool SubmissionOK = false;

            db.WriteTransaction(ref t, ref SubmissionOK,
                delegate
                {
                    OdbcParameter[] parameters = new OdbcParameter[1];
                    parameters[0] = new OdbcParameter("s_session_id_c", OdbcType.VarChar);
                    parameters[0].Value = FSessionID;

                    string sql = "DELETE FROM  s_session WHERE s_session_id_c = ?";
                    db.ExecuteNonQuery(sql, t, parameters);
                    SubmissionOK = true;
                });

            db.CloseDBConnection();
        }

        /// <summary>
        /// close the current session
        /// </summary>
        public static void CloseSession()
        {
            TLogging.LogAtLevel(1, "TSession.CloseSession got called: " + FSessionID);

            RemoveSession();

            FSessionID = String.Empty;
            FSessionValues = null;

            if (HttpContext.Current != null)
            {
                HttpContext.Current.Request.Cookies.Remove("OpenPetraSessionID");
                HttpContext.Current.Response.Cookies.Remove("OpenPetraSessionID");
                HttpContext.Current.Session.Clear();
            }
        }
    }
}
