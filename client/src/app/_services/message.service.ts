import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { TimeagoCustomFormatter } from 'ngx-timeago';
import { environment } from 'src/environments/environment';
import { Message } from '../_models/message';
import { getPaginatedResult, getPaginationHeaders } from './paginationHelper';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { BehaviorSubject } from 'rxjs';
import { User } from '../_models/User';
import { take } from 'rxjs/operators';
import { Group } from '../_models/group';
import { BusyService } from './busy.service';

@Injectable({
  providedIn: 'root',
})
export class MessageService {
  baseUrl = environment.apiUrl;
  hubUrl = environment.hubsUrl;
  private hubConnection?: HubConnection;
  private messageThreadSource = new BehaviorSubject<Message[]>([]);
  messageThreads$ = this.messageThreadSource.asObservable();

  constructor(private http: HttpClient,private busyService:BusyService) {}

  createHubConnection(user: User, otherUserName: string) {
    this.busyService.busy()
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(this.hubUrl + 'message?user=' + otherUserName, {
        accessTokenFactory: () => user.token,
      })
      .withAutomaticReconnect()
      .build();
    this.hubConnection.start()
      .catch((error) => console.log(error))
      .finally(()=>{
        this.busyService.idle()
      });

    this.hubConnection.on('ReceiveMessageThread', (message) => {
      this.messageThreadSource.next(message);
    });

    this.hubConnection.on("UpdatedGroup", (group:Group)=>{
      if(group.connections.some(x=>x.username === otherUserName)){
        this.messageThreads$.pipe(take(1)).subscribe({
          next:messages=>{
            messages.forEach(message=>{
              if(!message.dateRead){
                message.dateRead=new Date(Date.now())
              }
            })
            this.messageThreadSource.next([...messages])
          }
        })
      }
    })

    this.hubConnection.on('NewMessage', (message) => {
      this.messageThreads$.pipe(take(1)).subscribe({
        next: (messages) => {
          this.messageThreadSource.next([...messages, message]);
        },
      });
    });
  }

  stopHubConnection() {
    if (this.hubConnection) {      
      this.messageThreadSource.next([])
      this.hubConnection.stop();
    }
  }

  getMessages(pageNumber, pageSize, container) {
    let params = getPaginationHeaders(pageNumber, pageSize);
    params = params.append('Container', container);
    return getPaginatedResult<Partial<Message[]>>(
      this.baseUrl + 'messages',
      params,
      this.http
    );
  }

  getMessageThread(username: string) {
    return this.http.get<Message[]>(
      this.baseUrl + 'messages/thread/' + username
    );
  }

  async sendMessage(username: string, content: string) {
    return this.hubConnection.invoke('SendMessage', {
      recipientUsername: username,
      content,
    }).catch(error=>console.log(error));
  }

  deleteMessages(id: number) {
    return this.http.delete(this.baseUrl + 'messages/' + id);
  }
}
