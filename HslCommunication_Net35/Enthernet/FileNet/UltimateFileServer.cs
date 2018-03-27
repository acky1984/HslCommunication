﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.IO;
using System.Security.Cryptography;
using System.Drawing;
using HslCommunication.BasicFramework;
using HslCommunication.LogNet;
using HslCommunication.Core;

namespace HslCommunication.Enthernet
{
    /// <summary>
    /// 终极文件管理服务器，实现所有的文件分类管理，读写分离，不支持直接访问文件名
    /// </summary>

    public class UltimateFileServer : Core.Net.NetworkFileServerBase
    {
        #region Constructor

        /// <summary>
        /// 实例化一个对象
        /// </summary>
        public UltimateFileServer( )
        {

        }

        #endregion

        #region 文件列表存储器

        /// <summary>
        /// 所有文件组操作的词典锁
        /// </summary>
        internal Dictionary<string, GroupFileContainer> m_dictionary_group_marks = new Dictionary<string, GroupFileContainer>( );
        /// <summary>
        /// 词典的锁
        /// </summary>
        private SimpleHybirdLock hybirdLock = new SimpleHybirdLock( );

        /// <summary>
        /// 获取当前目录的读写锁，如果没有会自动创建
        /// </summary>
        /// <param name="filePath">相对路径名</param>
        /// <returns>读写锁</returns>
        public GroupFileContainer GetGroupFromFilePath( string filePath )
        {
            GroupFileContainer GroupFile = null;
            hybirdLock.Enter( );

            // lock operator
            if (m_dictionary_group_marks.ContainsKey( filePath ))
            {
                GroupFile = m_dictionary_group_marks[filePath];
            }
            else
            {
                GroupFile = new GroupFileContainer( LogNet, filePath );
                m_dictionary_group_marks.Add( filePath, GroupFile );
            }

            hybirdLock.Leave( );
            return GroupFile;
        }




        #endregion

        #region 接收文件数据并更新文件列表


        /// <summary>
        /// 从套接字接收文件并保存，更新文件列表
        /// </summary>
        /// <param name="socket">套接字</param>
        /// <param name="savename">保存的文件名</param>
        /// <returns></returns>
        private OperateResult ReceiveFileFromSocketAndUpdateGroup(
            Socket socket,
            string savename
            )
        {
            FileInfo info = new FileInfo( savename );
            string guidName = CreateRandomFileName( );
            string fileName = info.DirectoryName + "\\" + guidName;

            OperateResult<FileBaseInfo> receive = ReceiveFileFromSocket( socket, fileName, null );
            if(!receive.IsSuccess)
            {
                DeleteFileByName( fileName );
                return receive;
            }

            // 更新操作
            GroupFileContainer fileManagment = GetGroupFromFilePath( info.DirectoryName );
            string oldName = fileManagment.UpdateFileMappingName(
                info.Name,
                receive.Content.Size,
                guidName,
                receive.Content.Upload,
                receive.Content.Tag
                );

            // 删除旧的文件
            DeleteExsistingFile( info.DirectoryName, oldName );


            // 回发消息
            return SendStringAndCheckReceive( socket, 1, "成功" );
        }

        #endregion

        #region Private Method

        /// <summary>
        /// 根据文件的显示名称转化为真实存储的名称
        /// </summary>
        /// <param name="factory">第一大类</param>
        /// <param name="group">第二大类</param>
        /// <param name="id">第三大类</param>
        /// <param name="fileName">文件显示名称</param>
        /// <returns></returns>
        private string TransformFactFileName( string factory, string group, string id, string fileName )
        {
            string path = ReturnAbsoluteFilePath( factory, group, id );
            GroupFileContainer fileManagment = GetGroupFromFilePath( path );
            return fileManagment.GetCurrentFileMappingName( fileName );
        }

        private void DeleteExsistingFile( string path, string fileName )
        {
            if (!string.IsNullOrEmpty( fileName ))
            {
                string fileUltimatePath = path + "\\" + fileName;
                FileMarkId fileMarkId = GetFileMarksFromDictionaryWithFileName( fileName );

                fileMarkId.AddOperation( ( ) =>
                {
                    if (DeleteFileByName( fileUltimatePath ))
                    {
                        LogNet?.WriteInfo( ToString(), "文件删除成功:" + fileUltimatePath );
                    }
                } );
            }
        }

        #endregion

        #region Protect Override

        /// <summary>
        /// 处理数据
        /// </summary>
        /// <param name="obj"></param>
        protected override void ThreadPoolLogin( object obj )
        {
            if (obj is Socket socket)
            {

                OperateResult result = new OperateResult( );

                // 获取ip地址
                string IpAddress = ((IPEndPoint)(socket.RemoteEndPoint)).Address.ToString( );

                // 接收操作信息
                if (!ReceiveInformationHead(
                    socket,
                    out int customer,
                    out string fileName,
                    out string Factory,
                    out string Group,
                    out string Identify).IsSuccess)
                {
                    return;
                }

                string relativeName = ReturnRelativeFileName( Factory, Group, Identify, fileName );

                if (customer == HslProtocol.ProtocolFileDownload)
                {
                    // 先获取文件的真实名称
                    string guidName = TransformFactFileName( Factory, Group, Identify, fileName );
                    // 获取文件操作锁
                    FileMarkId fileMarkId = GetFileMarksFromDictionaryWithFileName( guidName );
                    fileMarkId.EnterReadOperator( );
                    // 发送文件数据
                    OperateResult send = SendFileAndCheckReceive( socket, ReturnAbsoluteFileName( Factory, Group, Identify, guidName ), fileName, "", "", null );
                    if(!send.IsSuccess)
                    {
                        fileMarkId.LeaveReadOperator( );
                        LogNet?.WriteError( ToString( ), $"{StringResources.FileDownloadFailed}:{relativeName} ip:{IpAddress}" );
                        return;
                    }
                    else
                    {
                        LogNet?.WriteInfo( ToString( ), StringResources.FileDownloadSuccess + ":" + relativeName );
                    }

                    fileMarkId.LeaveReadOperator( );
                    // 关闭连接
                    socket?.Close( );
                }
                else if (customer == HslProtocol.ProtocolFileUpload)
                {
                    string fullFileName = ReturnAbsoluteFileName( Factory, Group, Identify, fileName );
                    // 上传文件
                    CheckFolderAndCreate( );
                    FileInfo info = new FileInfo( fullFileName );

                    try
                    {
                        if (!Directory.Exists( info.DirectoryName ))
                        {
                            Directory.CreateDirectory( info.DirectoryName );
                        }
                    }
                    catch (Exception ex)
                    {
                        LogNet?.WriteException( ToString( ), "创建文件夹失败：" + fullFileName, ex );
                        socket?.Close( );
                        return;
                    }

                    // 接收文件并回发消息
                    if (ReceiveFileFromSocketAndUpdateGroup(
                        socket,                    // 网络套接字
                        fullFileName).IsSuccess)
                    {
                        socket?.Close( );
                        LogNet?.WriteInfo( ToString( ), StringResources.FileUploadSuccess + ":" + relativeName );
                    }
                    else
                    {
                        LogNet?.WriteInfo( ToString( ), StringResources.FileUploadFailed + ":" + relativeName );
                    }
                }
                else if (customer == HslProtocol.ProtocolFileDelete)
                {
                    string fullFileName = ReturnAbsoluteFileName( Factory, Group, Identify, fileName );

                    FileInfo info = new FileInfo( fullFileName );
                    GroupFileContainer fileManagment = GetGroupFromFilePath( info.DirectoryName );

                    // 新增删除的任务
                    DeleteExsistingFile( info.DirectoryName, fileManagment.DeleteFile( info.Name ) );

                    // 回发消息
                    if (SendStringAndCheckReceive(
                        socket,                                                                // 网络套接字
                        1,                                                                     // 没啥含义
                        "成功"                                                                // 没啥含意
                        ).IsSuccess)
                    {
                        socket?.Close( );
                    }

                    LogNet?.WriteInfo( ToString( ), StringResources.FileDeleteSuccess + ":" + relativeName );
                }
                else if (customer == HslProtocol.ProtocolFileDirectoryFiles)
                {
                    GroupFileContainer fileManagment = GetGroupFromFilePath( ReturnAbsoluteFilePath( Factory, Group, Identify ) );

                    if (SendStringAndCheckReceive(
                        socket,
                        HslProtocol.ProtocolFileDirectoryFiles,
                        fileManagment.JsonArrayContent ).IsSuccess)
                    {
                        socket?.Close( );
                    }
                }
                else if (customer == HslProtocol.ProtocolFileDirectories)
                {
                    List<string> folders = new List<string>( );
                    foreach (var m in GetDirectories( Factory, Group, Identify ))
                    {
                        DirectoryInfo directory = new DirectoryInfo( m );
                        folders.Add( directory.Name );
                    }

                    Newtonsoft.Json.Linq.JArray jArray = Newtonsoft.Json.Linq.JArray.FromObject( folders.ToArray( ) );
                    if (SendStringAndCheckReceive(
                        socket,
                        HslProtocol.ProtocolFileDirectoryFiles,
                        jArray.ToString( )).IsSuccess)
                    {
                        socket?.Close( );
                    }
                }
                else
                {
                    socket?.Close( );
                }
            }
        }


        #endregion

        #region Object Override

        /// <summary>
        /// 获取本对象的字符串表示形式
        /// </summary>
        /// <returns></returns>
        public override string ToString( )
        {
            return "UltimateFileServer";
        }

        #endregion
    }
}
