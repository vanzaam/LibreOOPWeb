﻿using Newtonsoft.Json;
using LibreOOPWeb.Helpers;
using LibreOOPWeb.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using LibreOOPWeb.Models;

namespace LibreOOPWeb.Controllers
{
    public class ApiController : Controller
    {
        


        private async Task<bool> checkUploadPermissions(string accesstoken){
            //return true;
            return await NightscoutPermissions.CheckUploadPermissions(accesstoken);
        }

        private async Task<bool> checkProcessingPermissions(string process_token){
            //return true;
            return await NightscoutPermissions.CheckProcessPermissions(process_token);
        }

        private ActionResult Error(string msg){
            return Json(
                new
                {
                    Error = true,
                    Message = msg
                },
                JsonRequestBehavior.AllowGet);
        } 
        private ActionResult Success<T>(T result, string command){
            return Json(
                new
                {
                    Error = false,
                    Command = command,
                    Result = result
                },
                JsonRequestBehavior.AllowGet);
        } 
        public async Task<ActionResult> DeleteTestReadings(string processing_accesstoken){
            if (!await this.checkProcessingPermissions(processing_accesstoken))
            {
                return this.Error("DeleteTestReadings Denied");
            }

            try
            {
                await MongoConnection.DeleteTestReadings();
            } catch(Exception ex) {
                return this.Error("DeleteTestReadings Failed: " + ex.Message);
            }

            return Success<string>("test readings deleted", "DeleteTestReadings");

        }
        public async Task<ActionResult> CreateRequestAsync(string accesstoken, string b64contents, string oldState, string sensorStartTimestamp, string sensorScanTimestamp, string currentUtcOffset){

            //var permissions= await NightscoutPermissions.CheckUploadPermissions(accesstoken);
            //var permissions = await NightscoutPermissions.CheckProcessPermissions(accesstoken);
            if (! await this.checkUploadPermissions(accesstoken))
            {
                return this.Error("CreateRequestAsync Denied");
            }
            long? longSensorStartTimestamp = null;
            long? longSensorScanTimestamp = null;
            long? longCurrentUtcOffset = null;


            if(string.IsNullOrWhiteSpace(b64contents)) {
                return this.Error("CreateRequestAsync Denied: invalid parameter b64contents");
            }
            try{
                // Database expects a base64, which we already have
                // this is just to verify that the contents is valid as base64
                System.Convert.FromBase64String(b64contents);
            } catch(FormatException){
                return this.Error("CreateRequestAsync Denied: invalid parameter b64contents: (not a b64string)");
            }

            if (string.IsNullOrWhiteSpace(oldState + sensorStartTimestamp + sensorScanTimestamp + currentUtcOffset))
            {
                //these advanced parameters are optional, and this is ok

            }
            else if (oldState?.Length > 0 && sensorStartTimestamp?.Length > 0 && sensorScanTimestamp?.Length > 0 && currentUtcOffset?.Length > 0)
            {
                try
                {
                    //database expects a base64, which we already have
                    // this is just to verify that the contents is valid as base64
                    System.Convert.FromBase64String(oldState);
                }
                catch (FormatException)
                {
                    return this.Error("CreateRequestAsync Denied: invalid parameter oldState: (not a b64string)");
                }
                longSensorStartTimestamp = Int64.TryParse(sensorStartTimestamp, out  var t1) ? t1 : (long?)null;
                longSensorScanTimestamp = Int64.TryParse(sensorScanTimestamp, out var t2) ? t2 : (long?)null;
                longCurrentUtcOffset = Int64.TryParse(currentUtcOffset, out var t3) ? t3 : (long?)null;

                if (longSensorStartTimestamp == null)
                {
                    return this.Error("CreateRequestAsync Denied: invalid parameter sensorStartTimestamp: (not a long)");
                }

                if (longSensorScanTimestamp == null)
                {
                    return this.Error("CreateRequestAsync Denied: invalid parameter sensorScanTimestamp: (not a long)");
                }

                if (longCurrentUtcOffset == null)
                {
                    return this.Error("CreateRequestAsync Denied: invalid parameter currentUtcOffset: (not a long)");
                }

            }
            else
            {
                return this.Error("CreateRequestAsync Denied: when speciying advanced parameters, all advanced parameters should be specified (oldState, sensorStartTimestamp, sensorScanTimestamp, currentUtcOffset)");
            }

            var g = Guid.NewGuid().ToString();
            var reading = new LibreReadingModel
            {
                CreatedOn = DateTime.Now,
                ModifiedOn = DateTime.Now,
                status = "pending",
                b64contents = b64contents,
                uuid = g,
                oldState = (oldState == null || oldState == "") ? null : oldState,
                currentUtcOffset = longCurrentUtcOffset,
                sensorStartTimestamp = longSensorStartTimestamp,
                sensorScanTimestamp = longSensorScanTimestamp,
                newState = null //gets updated by the algo

            };

            //return this.Error("synthax ok");
            try{
                await MongoConnection.AsyncInsertReading(reading);

            } catch(System.TimeoutException) {
                return Error("Timeout, database down?");
            }

            return Success<LibreReadingModel>(reading, "CreateRequestAsync");

            //var content = $"accesstoken: {accesstoken}, b64contents: {b64contents}, guid: {g}";
            //return Content("CreateRequestAsync IS NOT IMPLEMENTED YET:" + content);    
        }

        public async Task<ActionResult> Uptime(string upordown) {
            //this is an unprivileged operation
                var uptime = await MongoConnection.GetLatestPing();

            var now = DateTime.Now;
            int diff = -1;
           
            if (uptime != null){
                diff = Convert.ToInt32((now - uptime).Value.TotalSeconds);

            }
            if (upordown == "keyword") {
                return Content( (diff < 0 || diff > 60) ? "down" : "up");
            }
            return Success<UptimeModel>(new UptimeModel{ 
                LastPingSecondsAgo = diff,
                LastPing = uptime,
                Now = now
            }, "Uptime");

        }

        public async Task<ActionResult> GetStatus(string accesstoken, string uuid) {

            if (!await this.checkUploadPermissions(accesstoken))
            {
                return this.Error("GetStatus Denied");
            }

            if (string.IsNullOrWhiteSpace(uuid))
            {
                return this.Error("GetStatus Denied: invalid parameter uuid");
            }

            LibreReadingModel reading;

            try
            {
                reading = await MongoConnection.GetRemoteReading(uuid);

            }catch(Exception ex){
                return Error("GetStatus Failed: invalid uuid? " + ex.Message);
            }
            return Success<LibreReadingModel>(reading, "GetStatus");

            //var content = $"accesstoken: {accesstoken}, uuid: {uuid}, reading: {reading}";
            //return Content("GetStatus IS NOT IMPLEMENTED YET:" + content);
        }

        public async Task<ActionResult> FetchPendingRequests(string processing_accesstoken){
            if (!await this.checkProcessingPermissions(processing_accesstoken))
            {
                return this.Error("FetchPendingRequests Denied");
            }
            List<LibreReadingModel> readings;
            try
            {
                readings = await MongoConnection.GetPendingReadingsForProcessing(Config.MaxFetchPerAttempt);
            }
            catch (Exception ex)
            {
                return Error("FetchPendingRequests Failed: " + ex.Message);
            }

            try
            {
                await MongoConnection.UpdatePingCollection();
            }
            catch (Exception)
            {
            }

            return Success<List<LibreReadingModel>>(readings, "FetchPendingRequests");
            //var content = $"processing_accesstoken: {processing_accesstoken}";
            //return Content(System.Reflection.MethodBase.GetCurrentMethod().Name + " IS NOT IMPLEMENTED YET:" + content);
        }

        public async Task<ActionResult> UploadResults(string processing_accesstoken, string uuid, string result, string newState){
            if (!await this.checkProcessingPermissions(processing_accesstoken))
            {
                return this.Error("UploadResults Denied");
            }
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return this.Error("UploadResults Denied: invalid parameter uuid");
            }
            if (string.IsNullOrWhiteSpace(result))
            {
                return this.Error("UploadResults Denied: invalid parameter result");
            }

            var wasSuccessfullyModified = false;

            try
            {
                var reading = new LibreReadingModel
                {
                    
                    ModifiedOn = DateTime.Now,
                    status = "complete",
                    uuid = uuid,
                    result = result,
                    newState = newState


                };

                wasSuccessfullyModified = await MongoConnection.AsyncUpdateReading(reading);

            }
            catch (Exception ex)
            {
                return Error("UploadResults Failed: " + ex.Message);
            }

            return Success<string>($"Modified: {wasSuccessfullyModified}", "UploadResults");
            //var content = $"processing_accesstoken: {processing_accesstoken}, uuid: {uuid}, result: {result}";
            //return Content(System.Reflection.MethodBase.GetCurrentMethod().Name + " IS NOT IMPLEMENTED YET:" + content);
        }

        public ActionResult Index()
        {
            var content = @"
{ available_methods: ""
/api/CreateRequestAsync -> Object{uuid:String|null, error:false|true, msg:String}
 params:   -b64contents
  -accesstoken


/api/GetStatus -> Object{error:false|true, result:ResultsObject{ status: ""pending | processing | complete"", uuid:String, result:String<oopresults>} 
params:
  -accesstoken
  -uuid
""} 

";
            return Content(content, "application/json");
            //the minutes parameter is often 1440 (24 hours), telling you how long back you should do the search
            //In nightscout context it is mostly redundant as the maxCount will search as long back as needed.
            //we ignore that parameter
            //Logger.LogInfo("Accesing Glucose Index");


           
        }
    }
}
