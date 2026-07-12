# Gateway SocialGraph and Feed API

Tai lieu nay la contract frontend cho API da compose. Frontend chi goi:

```http
POST /graphql
Authorization: Bearer <access token>
Content-Type: application/json
```

Frontend khong gui `X-Gateway-Secret`, `X-User-Id`, `X-Session-Id` hoac `X-Username`. Gateway xoa cac header gia mao, validate JWT/session, roi tao trusted headers khi goi subgraph.

Snowflake `Long` co the vuot precision an toan cua JavaScript. Nen giu GraphQL `ID` o dang string; voi field `Long`, dung GraphQL scalar policy khong ep qua JavaScript `number` neu ID co the vuot `2^53 - 1`.

## Public Operations

```text
Query:    recommendFeed, visitedGroups, postDetail, postDetails,
          homeStories, myStories
Mutation: createUser, recordGroupVisit, createFeedPost,
          createNormalStory, createShareStory, deleteStory
```

## Recommended Feed

```graphql
query RecommendedFeed($userId: ID!, $skip: Int! = 0, $take: Int! = 20) {
  recommendFeed(userId: $userId, skip: $skip, take: $take) {
    postId
    post {
      __typename
      ... on FeedPostDetail {
        id
        type
        content
        privacy
        create
        author { id name avatar isVerified canFollow }
        media { id type url }
      }
      ... on GroupPostDetail {
        id
        type
        content
        privacy
        create
        author { id name avatar isVerified canFollow }
        group { id name avatar canJoin }
        media { id type url }
      }
    }
  }
}
```

```json
{
  "userId": "9000000000000001",
  "skip": 0,
  "take": 20
}
```

- `take` duoc clamp `1..100`; `skip` toi thieu `0`.
- Thu tu item la thu tu rank cua Recommendation.
- `post` la nullable. Bo item neu `post == null`; truong hop nay xay ra khi post bi xoa, block, private, hoac graph data khong hop le sau luc rank.
- `FeedPostDetail` la bai user. `GroupPostDetail` luon co them `group`.
- Score ranking khong nam trong public contract.

## Group Shortcuts

Khi user mo group:

```graphql
mutation RecordGroupVisit($userId: Long!, $groupId: Long!) {
  recordGroupVisit(userId: $userId, groupId: $groupId)
}
```

Lay shortcut:

```graphql
query VisitedGroups($userId: Long!, $limit: Int!, $cursor: String) {
  visitedGroups(userId: $userId, limit: $limit, cursor: $cursor) {
    items { id name avatar }
    endCursor
    hasNextPage
  }
}
```

`limit` clamp `1..100`. Cursor la opaque keyset cursor; frontend chi luu va truyen lai `endCursor`. Chi load tiep khi `hasNextPage == true`. Private group khong con quyen xem se bi omit.

## Post Detail

Single post:

```graphql
query PostDetail($postId: Long!) {
  postDetail(postId: $postId) {
    __typename
    ... on FeedPostDetail {
      id type content privacy create
      author { id name avatar isVerified canFollow }
      media { id type url }
    }
    ... on GroupPostDetail {
      id type content privacy create
      author { id name avatar isVerified canFollow }
      group { id name avatar canJoin }
      media { id type url }
    }
  }
}
```

Batch hydration dung cho list ID da co san:

```graphql
query PostDetails($postIds: [Long!]!) {
  postDetails(postIds: $postIds) {
    __typename
    ... on FeedPostDetail { id content author { id name avatar } }
    ... on GroupPostDetail { id content group { id name avatar canJoin } }
  }
}
```

Toi da 100 IDs. Output giu thu tu input, bo duplicate va omit item deleted/unauthorized. Do output co the ngan hon input, map bang `id`, khong map bang index.

## Create Feed Post

```graphql
mutation CreateFeedPost($input: CreateFeedPostInput!) {
  createFeedPost(input: $input) {
    id
    type
    content
    privacy
    create
    authorId
    media { id type url }
  }
}
```

```json
{
  "input": {
    "authorId": 9000000000000001,
    "content": "Hello feed",
    "privacy": 0,
    "media": [
      { "type": 0, "url": "https://cdn.example/post.jpg" }
    ]
  }
}
```

`authorId` phai khop authenticated user. Frontend upload media truoc va gui URL. Search index va Recommendation embedding la best-effort projection sau khi SocialGraph tao post.

## Stories

```graphql
query HomeStories($userId: Long!, $limit: Int!, $cursor: String) {
  homeStories(userId: $userId, limit: $limit, cursor: $cursor) {
    items {
      author { id name avatar isVerified }
      latestCreate
      stories {
        __typename
        ... on NormalStory { id content create media { id type url } }
        ... on FeedPostShareStory {
          id content create
          sharedSource { id content media { id type url } author { id name avatar isVerified } }
        }
        ... on ReelShareStory {
          id content create
          sharedSource { id content media { id type url } author { id name avatar isVerified } }
        }
      }
    }
    endCursor
    hasNextPage
  }
}
```

`homeStories` paging theo author bucket, limit clamp `1..50`. `myStories(userId)` tra bucket cua viewer hoac null. Mutation canonical la `createNormalStory`, `createShareStory`, `deleteStory`; khong co `createStory`.

## Error Handling

- `UNAUTHENTICATED`: thieu/het han token, invalid session, hoac khong co trusted viewer.
- `FORBIDDEN`: `userId`/`authorId` khong khop authenticated user.
- `BAD_USER_INPUT`: batch post vuot 100 IDs hoac Recommendation `userId` khong phai positive signed 64-bit.
- Nullable/omitted post khong phai GraphQL error; day la ket qua privacy/deletion race hop le.
