﻿/*
This source file is subject to version 3 of the GPL license, 
that is bundled with this package in the file LICENSE, and is 
available online at http://www.gnu.org/licenses/gpl.txt; 
you may not use this file except in compliance with the License. 

Software distributed under the License is distributed on an "AS IS" basis,
WITHOUT WARRANTY OF ANY KIND, either express or implied. See the License for
the specific language governing rights and limitations under the License.

All portions of the code written by Voat are Copyright (c) 2014 Voat
All Rights Reserved.
*/

using System;
using System.Net;
using System.Threading.Tasks;
using System.Web.Mvc;
using Voat.Models;
using Voat.Utils;
using Voat.Utils.Components;

namespace Voat.Controllers
{
    public class CommentController : Controller
    {
        private readonly whoaverseEntities _db = new whoaverseEntities();
        readonly Random _rnd = new Random();

        // POST: votecomment/{commentId}/{typeOfVote}
        [Authorize]
        public JsonResult VoteComment(int commentId, int typeOfVote)
        {
            var loggedInUser = User.Identity.Name;

            switch (typeOfVote)
            {
                case 1:
                    if (Karma.CommentKarma(loggedInUser) > 20)
                    {
                        // perform upvoting or resetting
                        VotingComments.UpvoteComment(commentId, loggedInUser);
                    }
                    else if (Utils.User.TotalVotesUsedInPast24Hours(User.Identity.Name) < 11)
                    {
                        // perform upvoting or resetting even if user has no CCP but only allow 10 votes per 24 hours
                        VotingComments.UpvoteComment(commentId, loggedInUser);
                    }
                    break;
                case -1:
                    if (Karma.CommentKarma(loggedInUser) > 100)
                    {
                        // perform downvoting or resetting
                        VotingComments.DownvoteComment(commentId, loggedInUser);
                    }
                    break;
            }

            Response.StatusCode = 200;
            return Json("Voting ok", JsonRequestBehavior.AllowGet);
        }

        // GET: comments for a given submission
        public ActionResult Comments(int? id, string subversetoshow, int? startingcommentid, string sort)
        {
            var subverse = _db.Subverses.Find(subversetoshow);

            if (subverse != null)
            {
                ViewBag.SelectedSubverse = subverse.name;
                ViewBag.SubverseAnonymized = subverse.anonymized_mode;

                if (startingcommentid != null)
                {
                    ViewBag.StartingCommentId = startingcommentid;
                }

                if (sort != null)
                {
                    ViewBag.SortingMode = sort;
                }

                if (id == null)
                {
                    return View("~/Views/Errors/Error.cshtml");
                }

                var message = _db.Messages.Find(id);

                if (message == null)
                {
                    return View("~/Views/Errors/Error_404.cshtml");
                }

                // make sure that the combination of selected subverse and message subverse are linked
                if (!message.Subverse.Equals(subversetoshow, StringComparison.OrdinalIgnoreCase))
                {
                    return View("~/Views/Errors/Error_404.cshtml");
                }

                // experimental
                // register a new session for this subverse
                try
                {
                    var currentSubverse = (string)RouteData.Values["subversetoshow"];
                    SessionTracker.Add(currentSubverse, Session.SessionID);
                }
                catch (Exception)
                {
                    //
                }

                // check if this is a new view and register it
                string clientIpAddress = String.Empty;

                if (Request.ServerVariables["HTTP_X_FORWARDED_FOR"] != null)
                {
                    clientIpAddress = Request.ServerVariables["HTTP_X_FORWARDED_FOR"];
                }
                else if (Request.UserHostAddress.Length != 0)
                {
                    clientIpAddress = Request.UserHostAddress;
                }


                if (clientIpAddress != String.Empty)
                {

                    // generate salted hash of client IP address
                    string ipHash = IpHash.CreateHash(clientIpAddress);

                    // check if this hash is present for this submission id in viewstatistics table
                    var existingView = _db.Viewstatistics.Find(message.Id, ipHash);

                    // this hash doesn't already exist, register the view
                    if (existingView == null)
                    {

                        // this is a new view, register it for this submission
                        var view = new Viewstatistic { submissionId = message.Id, viewerId = ipHash };
                        _db.Viewstatistics.Add(view);
                        message.Views++;
                        _db.SaveChanges();
                    }

                }

                return View("~/Views/Home/Comments.cshtml", message);

            }
            return View("~/Views/Errors/Error_404.cshtml");
        }

        // GET: submitcomment
        public ActionResult SubmitComment()
        {
            return View("~/Views/Errors/Error_404.cshtml");
        }

        // POST: submitcomment, adds a new root comment
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [Authorize]
        [PreventSpam(DelayRequest = 120, ErrorMessage = "Sorry, you are doing that too fast. Please try again later.")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> SubmitComment([Bind(Include = "Id, CommentContent, MessageId, ParentId")] Comment comment)
        {
            comment.Date = DateTime.Now;
            comment.Name = User.Identity.Name;
            comment.Votes = 0;
            comment.Likes = 0;

            if (ModelState.IsValid)
            {
                // flag the comment as anonymized if it was submitted to a sub which has active anonymized_mode
                var message = _db.Messages.Find(comment.MessageId);
                if (message != null && (message.Anonymized || message.Subverses.anonymized_mode))
                {
                    comment.Anonymized = true;
                }

                // check if author is banned, don't save the comment or send notifications if true
                if (!Utils.User.IsUserGloballyBanned(User.Identity.Name) && !Utils.User.IsUserBannedFromSubverse(User.Identity.Name, message.Subverse))
                {
                    _db.Comments.Add(comment);

                    if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPreSave))
                    {
                        comment.CommentContent = ContentProcessor.Instance.Process(comment.CommentContent, ProcessingStage.InboundPreSave, comment);
                    }

                    await _db.SaveChangesAsync();

                    if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPostSave))
                    {
                        ContentProcessor.Instance.Process(comment.CommentContent, ProcessingStage.InboundPostSave, comment);
                    }

                    // send comment reply notification to parent comment author if the comment is not a new root comment
                    await NotificationManager.SendCommentNotification(comment);
                }

                if (Request.UrlReferrer != null)
                {
                    var url = Request.UrlReferrer.AbsolutePath;
                    return Redirect(url);
                }
            }
            if (Request.IsAjaxRequest())
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }

            ModelState.AddModelError(String.Empty, "Sorry, you are either banned fromt this sub or doing that too fast. Please try again in 2 minutes.");
            return View("~/Views/Help/SpeedyGonzales.cshtml");
        }

        // POST: editcomment
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [Authorize]
        [PreventSpam(DelayRequest = 15, ErrorMessage = "Sorry, you are doing that too fast. Please try again later.")]
        public async Task<ActionResult> EditComment([Bind(Include = "Id, CommentContent")] Comment model)
        {
            if (ModelState.IsValid)
            {
                var existingComment = _db.Comments.Find(model.Id);

                if (existingComment != null)
                {
                    if (existingComment.Name.Trim() == User.Identity.Name)
                    {
                        existingComment.LastEditDate = DateTime.Now;
                        var escapedCommentContent = WebUtility.HtmlEncode(model.CommentContent);
                        existingComment.CommentContent = escapedCommentContent;

                        if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPreSave))
                        {
                            existingComment.CommentContent = ContentProcessor.Instance.Process(existingComment.CommentContent, ProcessingStage.InboundPreSave, existingComment);
                        }

                        await _db.SaveChangesAsync();

                        if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPostSave))
                        {
                            ContentProcessor.Instance.Process(existingComment.CommentContent, ProcessingStage.InboundPostSave, existingComment);
                        }

                        // parse the new comment through markdown formatter and then return the formatted comment so that it can replace the existing html comment which just got modified
                        var formattedComment = Formatting.FormatMessage(WebUtility.HtmlDecode(existingComment.CommentContent));
                        return Json(new { response = formattedComment });
                    }
                    return Json("Unauthorized edit.", JsonRequestBehavior.AllowGet);
                }
            }

            if (Request.IsAjaxRequest())
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }

            return Json("Unauthorized edit or comment not found - comment ID was.", JsonRequestBehavior.AllowGet);
        }

        // POST: deletecomment
        [HttpPost]
        [Authorize]
        public async Task<ActionResult> DeleteComment(int commentId)
        {
            var commentToDelete = _db.Comments.Find(commentId);

            if (commentToDelete != null)
            {
                var commentSubverse = commentToDelete.Message.Subverse;

                // delete comment if the comment author is currently logged in user
                if (commentToDelete.Name == User.Identity.Name)
                {
                    commentToDelete.Name = "deleted";
                    commentToDelete.CommentContent = "deleted by author at " + DateTime.Now;
                    await _db.SaveChangesAsync();
                }
                // delete comment if delete request is issued by subverse moderator
                else if (Utils.User.IsUserSubverseAdmin(User.Identity.Name, commentSubverse) || Utils.User.IsUserSubverseModerator(User.Identity.Name, commentSubverse))
                {
                    // notify comment author that his comment has been deleted by a moderator
                    MesssagingUtility.SendPrivateMessage(
                        "Whoaverse",
                        commentToDelete.Name,
                        "Your comment has been deleted by a moderator",
                        "Your [comment](/v/" + commentSubverse + "/comments/" + commentToDelete.MessageId + "/" + commentToDelete.Id + ") has been deleted by: " +
                        "[" + User.Identity.Name + "](/u/" + User.Identity.Name + ")" + " on: " + DateTime.Now + "  " + Environment.NewLine +
                        "Original comment content was: " + Environment.NewLine +
                        "---" + Environment.NewLine +
                        commentToDelete.CommentContent
                        );

                    commentToDelete.Name = "deleted";
                    commentToDelete.CommentContent = "deleted by a moderator at " + DateTime.Now;
                    await _db.SaveChangesAsync();
                }
            }

            var url = Request.UrlReferrer.AbsolutePath;
            return Redirect(url);
        }

        // POST: comments/distinguish/{commentId}
        [Authorize]
        public JsonResult DistinguishComment(int commentId)
        {
            var commentToDistinguish = _db.Comments.Find(commentId);

            if (commentToDistinguish != null)
            {
                // check to see if request came from comment author
                if (User.Identity.Name == commentToDistinguish.Name)
                {
                    // check to see if comment author is also sub mod or sub admin for comment sub
                    if (Utils.User.IsUserSubverseAdmin(User.Identity.Name, commentToDistinguish.Message.Subverse) || Utils.User.IsUserSubverseModerator(User.Identity.Name, commentToDistinguish.Message.Subverse))
                    {
                        // mark the comment as distinguished and save to db
                        if (commentToDistinguish.IsDistinguished)
                        {
                            commentToDistinguish.IsDistinguished = false;
                        }
                        else
                        {
                            commentToDistinguish.IsDistinguished = true;
                        }

                        _db.SaveChangesAsync();

                        Response.StatusCode = 200;
                        return Json("Distinguish flag changed.", JsonRequestBehavior.AllowGet);
                    }
                }
            }

            Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return Json("Unauthorized distinguish attempt.", JsonRequestBehavior.AllowGet);
        }
    }
}